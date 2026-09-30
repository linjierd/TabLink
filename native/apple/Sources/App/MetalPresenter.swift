import Foundation
import MetalKit
import CoreVideo
import UIKit

final class MetalPresenter: NSObject, MTKViewDelegate {
    private let commandQueue: MTLCommandQueue
    private let pipeline: MTLRenderPipelineState
    private var textureCache: CVMetalTextureCache?
    private let lock = NSLock()
    private var latest: DecodedFrame?
    private var submittedID: UInt64 = 0
    private var submittedConnection: UUID?
    private var needsRedraw = true
    private let inFlight = DispatchSemaphore(value: 3)
    var onPresented: ((DecodedFrame, Double) -> Void)?
    var onFailure: ((String) -> Void)?

    init(view: MTKView) throws {
        guard let device = view.device, let queue = device.makeCommandQueue(), let library = device.makeDefaultLibrary(),
              let vertex = library.makeFunction(name: "tablinkVertex"), let fragment = library.makeFunction(name: "tablinkFragment") else {
            throw TabLinkError.invalid("无法初始化 Metal 显示器")
        }
        commandQueue = queue
        let descriptor = MTLRenderPipelineDescriptor()
        descriptor.vertexFunction = vertex; descriptor.fragmentFunction = fragment
        descriptor.colorAttachments[0].pixelFormat = view.colorPixelFormat
        pipeline = try device.makeRenderPipelineState(descriptor: descriptor)
        super.init()
        guard CVMetalTextureCacheCreate(kCFAllocatorDefault, nil, device, nil, &textureCache) == kCVReturnSuccess else {
            throw TabLinkError.invalid("无法建立视频纹理缓存")
        }
        view.delegate = self
    }

    func offer(_ frame: DecodedFrame) {
        lock.lock(); defer { lock.unlock() }
        if let latest, latest.connectionID == frame.connectionID && latest.id >= frame.id { return }
        latest = frame
    }

    func clear() {
        lock.lock(); latest = nil; submittedID = 0; submittedConnection = nil; needsRedraw = true; lock.unlock()
        if let textureCache { CVMetalTextureCacheFlush(textureCache, 0) }
    }

    var imageSize: CGSize {
        lock.lock(); defer { lock.unlock() }
        return latest.map { CGSize(width: CGFloat($0.width), height: CGFloat($0.height)) } ?? .zero
    }

    func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {
        lock.lock(); needsRedraw = true; lock.unlock()
    }

    func draw(in view: MTKView) {
        lock.lock()
        guard let frame = latest else {
            let shouldClear = needsRedraw; lock.unlock()
            if shouldClear, let drawable = view.currentDrawable, let descriptor = view.currentRenderPassDescriptor,
               let command = commandQueue.makeCommandBuffer(), let encoder = command.makeRenderCommandEncoder(descriptor: descriptor) {
                encoder.endEncoding()
                lock.lock(); needsRedraw = false; lock.unlock()
                command.present(drawable); command.commit() // Black clear is never acknowledged as a received frame.
            }
            return
        }
        guard needsRedraw || submittedConnection != frame.connectionID || submittedID != frame.id else { lock.unlock(); return }
        lock.unlock()
        guard inFlight.wait(timeout: .now()) == .success else { return }
        var committed = false
        defer { if !committed { inFlight.signal() } }
        guard let textureCache, let drawable = view.currentDrawable, let descriptor = view.currentRenderPassDescriptor,
              let command = commandQueue.makeCommandBuffer() else { return }
        var cvTexture: CVMetalTexture?
        let result = CVMetalTextureCacheCreateTextureFromImage(kCFAllocatorDefault, textureCache, frame.pixelBuffer, nil,
            .bgra8Unorm, frame.width, frame.height, 0, &cvTexture)
        guard result == kCVReturnSuccess, let cvTexture, let texture = CVMetalTextureGetTexture(cvTexture),
              let encoder = command.makeRenderCommandEncoder(descriptor: descriptor) else {
            onFailure?("无法将视频图像提交到 Metal"); return
        }
        let viewWidth = max(1, view.drawableSize.width), viewHeight = max(1, view.drawableSize.height)
        let scale = min(viewWidth / CGFloat(frame.width), viewHeight / CGFloat(frame.height))
        var fit = SIMD2<Float>(Float(CGFloat(frame.width) * scale / viewWidth), Float(CGFloat(frame.height) * scale / viewHeight))
        encoder.setRenderPipelineState(pipeline)
        encoder.setVertexBytes(&fit, length: MemoryLayout<SIMD2<Float>>.stride, index: 0)
        encoder.setFragmentTexture(texture, index: 0)
        encoder.drawPrimitives(type: .triangleStrip, vertexStart: 0, vertexCount: 4)
        encoder.endEncoding()
        // The only source of 0x12 telemetry. GPU completion alone is not presentation.
        drawable.addPresentedHandler { [weak self] presented in
            guard presented.presentedTime > 0 else { return }
            self?.onPresented?(frame, presented.presentedTime)
        }
        command.addCompletedHandler { [semaphore = inFlight, frame, cvTexture] _ in
            // Retain CVMetalTexture and its pixel buffer through GPU consumption.
            withExtendedLifetime((frame, cvTexture)) { semaphore.signal() }
        }
        lock.lock(); submittedConnection = frame.connectionID; submittedID = frame.id; needsRedraw = false; lock.unlock()
        command.present(drawable); command.commit(); committed = true
    }
}

final class TouchMetalView: MTKView {
    var imageSize: (() -> CGSize)?
    var onMouse: ((MouseInput) -> Void)?
    private var activeTouch: UITouch?
    private var lastPoint = CGPoint.zero
    private var lastMove: TimeInterval = 0

    private func normalize(_ point: CGPoint) -> CGPoint? {
        guard let size = imageSize?(), size.width > 0, size.height > 0 else { return nil }
        let scale = min(bounds.width / size.width, bounds.height / size.height)
        let rect = CGRect(x: (bounds.width - size.width * scale) / 2, y: (bounds.height - size.height * scale) / 2,
                          width: size.width * scale, height: size.height * scale)
        guard rect.contains(point) else { return nil }
        return CGPoint(x: (point.x - rect.minX) / rect.width, y: (point.y - rect.minY) / rect.height)
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard activeTouch == nil, let touch = touches.first, let point = normalize(touch.location(in: self)) else { return }
        activeTouch = touch; lastPoint = point; lastMove = touch.timestamp
        onMouse?(MouseInput(kind: "down", x: Double(point.x), y: Double(point.y)))
    }
    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard let activeTouch, touches.contains(activeTouch) else { return }
        guard let point = normalize(activeTouch.location(in: self)) else { releaseTouch(); return }
        lastPoint = point
        if activeTouch.timestamp - lastMove >= 1.0 / 60 {
            lastMove = activeTouch.timestamp; onMouse?(MouseInput(kind: "move", x: Double(point.x), y: Double(point.y)))
        }
    }
    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        guard let activeTouch, touches.contains(activeTouch) else { return }
        if let point = normalize(activeTouch.location(in: self)) { lastPoint = point }
        releaseTouch()
    }
    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) { releaseTouch() }
    func releaseTouch() {
        if activeTouch != nil { onMouse?(MouseInput(kind: "up", x: Double(lastPoint.x), y: Double(lastPoint.y))) }
        activeTouch = nil
    }
}
