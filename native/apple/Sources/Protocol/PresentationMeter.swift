import Foundation

struct PresentedReport: Encodable {
    let kind = "frame-presented"
    let sequence: UInt64
    let width: Int
    let height: Int
    let fps: Double
    let codec: String
    let decoder: String
}

/// Invoke only for an actual presented drawable; never for decode, submit or GPU completion.
struct PresentationMeter {
    private(set) var count: UInt64 = 0
    private var lastFrameID: UInt64 = 0
    private var lastTime: Double = 0
    private var lastReportTime: Double?
    private var rateStart: Double?
    private var rateStartCount: UInt64 = 0
    private var fps: Double = 0

    mutating func presented(id: UInt64, time: Double, width: Int, height: Int, codec: String = "H.264") -> PresentedReport? {
        guard id > lastFrameID, time.isFinite, time > lastTime, width > 0, height > 0 else { return nil }
        lastFrameID = id; lastTime = time; count += 1
        if let start = rateStart, time - start >= 1 {
            fps = Double(count - rateStartCount) / (time - start); rateStart = time; rateStartCount = count
        } else if rateStart == nil { rateStart = time; rateStartCount = count }
        guard lastReportTime == nil || time - lastReportTime! >= 1 else { return nil }
        lastReportTime = time
        return PresentedReport(sequence: count, width: width, height: height, fps: fps, codec: codec,
            decoder: codec == "H.264" ? "VideoToolbox hardware + Metal presented callback" : "ImageIO + Metal presented callback")
    }
}
