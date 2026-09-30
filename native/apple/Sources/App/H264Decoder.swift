import Foundation
import VideoToolbox
import CoreMedia
import CoreVideo

struct DecodedFrame {
    let connectionID: UUID
    let id: UInt64
    let pts: Int64
    let pixelBuffer: CVPixelBuffer
    let codec: String
    var width: Int { CVPixelBufferGetWidth(pixelBuffer) }
    var height: Int { CVPixelBufferGetHeight(pixelBuffer) }
}

/// Serial decoder submission with four in-flight buffers; compressed reference frames are never dropped.
final class H264Decoder {
    private final class FrameContext {
        let id: UInt64, connectionID: UUID
        init(id: UInt64, connectionID: UUID) { self.id = id; self.connectionID = connectionID }
    }
    private let queue = DispatchQueue(label: "TabLink.VideoToolbox", qos: .userInteractive)
    private let slots = DispatchSemaphore(value: 4)
    private let pendingLock = NSLock()
    private var pendingFrames: [UInt64: FrameContext] = [:]
    private var session: VTDecompressionSession?
    private var format: CMVideoFormatDescription?
    private var closed = false
    private var awaitingKeyFrame = true
    private let connectionID: UUID
    private let configuration: VideoConfiguration
    var onFrame: ((DecodedFrame) -> Void)?
    var onFailure: ((String) -> Void)?

    init(configuration: VideoConfiguration, connectionID: UUID) {
        self.configuration = configuration; self.connectionID = connectionID
    }

    func configure(completion: @escaping (Result<Void, Error>) -> Void) {
        queue.async { [self] in
            guard !closed else { completion(.failure(TabLinkError.invalid("解码器已关闭"))); return }
            do {
                let (sps, pps) = try configuration.parameterSets()
                var description: CMFormatDescription?
                let status: OSStatus = sps.withUnsafeBytes { spsBuffer in
                    pps.withUnsafeBytes { ppsBuffer in
                        let pointers = [spsBuffer.bindMemory(to: UInt8.self).baseAddress!, ppsBuffer.bindMemory(to: UInt8.self).baseAddress!]
                        let lengths = [sps.count, pps.count]
                        return pointers.withUnsafeBufferPointer { pointerBuffer in
                            lengths.withUnsafeBufferPointer { lengthBuffer in
                                CMVideoFormatDescriptionCreateFromH264ParameterSets(allocator: kCFAllocatorDefault,
                                    parameterSetCount: 2, parameterSetPointers: pointerBuffer.baseAddress!,
                                    parameterSetSizes: lengthBuffer.baseAddress!, nalUnitHeaderLength: 4,
                                    formatDescriptionOut: &description)
                            }
                        }
                    }
                }
                guard status == noErr, let description else { throw TabLinkError.invalid("无法读取 H.264 格式参数") }
                let dimensions = CMVideoFormatDescriptionGetDimensions(description)
                guard dimensions.width > 0, dimensions.height > 0,
                      dimensions.width <= 8192, dimensions.height <= 8192,
                      Int64(dimensions.width) * Int64(dimensions.height) <= 16_000_000,
                      Int(dimensions.width) == configuration.width, Int(dimensions.height) == configuration.height else {
                    throw TabLinkError.invalid("H.264 实际参数超出尺寸限制")
                }
                format = description
                var callback = VTDecompressionOutputCallbackRecord(decompressionOutputCallback: { refcon, frameRef, status, _, image, pts, _ in
                    guard let refcon, let frameRef else { return }
                    let decoder = Unmanaged<H264Decoder>.fromOpaque(refcon).takeUnretainedValue()
                    guard let context = decoder.finishFrame(UInt64(UInt(bitPattern: frameRef))) else { return }
                    guard status == noErr else { decoder.onFailure?("硬件解码未能输出图像"); return }
                    guard let image else { return } // A reported dropped frame has no presentation to acknowledge.
                    let microseconds = CMTimeConvertScale(pts, timescale: 1_000_000, method: .default).value
                    decoder.onFrame?(DecodedFrame(connectionID: context.connectionID, id: context.id,
                        pts: microseconds, pixelBuffer: image, codec: "H.264"))
                }, decompressionOutputRefCon: Unmanaged.passUnretained(self).toOpaque())
                let attributes: [String: Any] = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
                    kCVPixelBufferMetalCompatibilityKey as String: true,
                    kCVPixelBufferIOSurfacePropertiesKey as String: [:] as [String: Any]]
                let specification: [String: Any] = [kVTVideoDecoderSpecification_RequireHardwareAcceleratedVideoDecoder as String: true]
                let createStatus = VTDecompressionSessionCreate(allocator: kCFAllocatorDefault, formatDescription: description,
                    decoderSpecification: specification as CFDictionary, imageBufferAttributes: attributes as CFDictionary,
                    outputCallback: &callback, decompressionSessionOut: &session)
                guard createStatus == noErr, let session else { throw TabLinkError.invalid("此设备无法创建所需 H.264 硬件解码器") }
                var hardware: CFTypeRef?
                let queryStatus = VTSessionCopyProperty(session, key: kVTDecompressionPropertyKey_UsingHardwareAcceleratedVideoDecoder,
                    allocator: kCFAllocatorDefault, valueOut: &hardware)
                guard queryStatus == noErr, (hardware as? NSNumber)?.boolValue == true else {
                    VTDecompressionSessionInvalidate(session); self.session = nil
                    throw TabLinkError.invalid("没有确认硬件解码，已停止播放")
                }
                VTSessionSetProperty(session, key: kVTDecompressionPropertyKey_RealTime, value: kCFBooleanTrue)
                completion(.success(()))
            } catch { completion(.failure(error)) }
        }
    }

    func decode(_ accessUnit: VideoAccessUnit, id: UInt64, completion: @escaping (Result<Void, Error>) -> Void) {
        queue.async { [self] in
            guard !closed, let session, let format else { completion(.failure(TabLinkError.invalid("尚未建立解码器"))); return }
            if awaitingKeyFrame && !accessUnit.isKeyFrame { completion(.success(())); return }
            awaitingKeyFrame = false
            guard slots.wait(timeout: .now() + 2) == .success else {
                completion(.failure(TabLinkError.invalid("硬件解码队列超时"))); return
            }
            var slotIsOwned = true
            do {
                var block: CMBlockBuffer?
                var status = CMBlockBufferCreateWithMemoryBlock(allocator: kCFAllocatorDefault, memoryBlock: nil,
                    blockLength: accessUnit.avcc.count, blockAllocator: kCFAllocatorDefault, customBlockSource: nil,
                    offsetToData: 0, dataLength: accessUnit.avcc.count, flags: 0, blockBufferOut: &block)
                guard status == kCMBlockBufferNoErr, let block else { throw TabLinkError.invalid("无法分配视频输入") }
                status = accessUnit.avcc.withUnsafeBytes { bytes in
                    CMBlockBufferReplaceDataBytes(with: bytes.baseAddress!, blockBuffer: block, offsetIntoDestination: 0, dataLength: bytes.count)
                }
                guard status == noErr else { throw TabLinkError.invalid("无法复制视频输入") }
                var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: Int32(configuration.fps.rounded())),
                    presentationTimeStamp: CMTime(value: accessUnit.ptsMicroseconds, timescale: 1_000_000), decodeTimeStamp: .invalid)
                var size = accessUnit.avcc.count
                var sample: CMSampleBuffer?
                status = CMSampleBufferCreateReady(allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: format,
                    sampleCount: 1, sampleTimingEntryCount: 1, sampleTimingArray: &timing,
                    sampleSizeEntryCount: 1, sampleSizeArray: &size, sampleBufferOut: &sample)
                guard status == noErr, let sample else { throw TabLinkError.invalid("无法创建视频采样") }
                // An integer key, never a dereferenced/retained pointer. A synchronously
                // dropped frame is allowed to have no output callback.
                pendingLock.lock(); pendingFrames[id] = FrameContext(id: id, connectionID: connectionID); pendingLock.unlock()
                slotIsOwned = false
                let context = UnsafeMutableRawPointer(bitPattern: UInt(id))!
                var info: VTDecodeInfoFlags = []
                status = VTDecompressionSessionDecodeFrame(session, sampleBuffer: sample, flags: [.enableAsynchronousDecompression],
                    frameRefcon: context, infoFlagsOut: &info)
                if status != noErr {
                    _ = finishFrame(id)
                    throw TabLinkError.invalid("H.264 硬件解码拒绝了输入")
                }
                if info.contains(.frameDropped) { _ = finishFrame(id) }
                // finishFrame releases each context/slot at most once, even for synchronous callbacks.
                completion(.success(()))
            } catch { if slotIsOwned { slots.signal() }; completion(.failure(error)) }
        }
    }

    private func finishFrame(_ id: UInt64) -> FrameContext? {
        pendingLock.lock(); let context = pendingFrames.removeValue(forKey: id); pendingLock.unlock()
        if context != nil { slots.signal() }
        return context
    }

    func close(completion: (() -> Void)? = nil) {
        queue.async { [self] in
            closed = true
            if let session {
                VTDecompressionSessionWaitForAsynchronousFrames(session)
                VTDecompressionSessionInvalidate(session)
            }
            pendingLock.lock(); let remaining = pendingFrames.count; pendingFrames.removeAll(); pendingLock.unlock()
            for _ in 0..<remaining { slots.signal() }
            session = nil; format = nil; onFrame = nil; onFailure = nil
            completion?()
        }
    }
}
