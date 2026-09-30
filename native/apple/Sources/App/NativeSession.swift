import Foundation

final class NativeSession {
    private let queue = DispatchQueue(label: "TabLink.Network", qos: .userInteractive)
    private let link: PairingLink
    private var profile: Data
    private var transport: NativeTransport?
    private var decoder: H264Decoder?
    private var active = false
    private var connectionID = UUID()
    private var receivedFrames: UInt64 = 0
    private var presentation = PresentationMeter()
    private var retry = 0
    var onStatus: ((String, Bool) -> Void)?
    var onFrame: ((DecodedFrame) -> Void)?
    var onReset: (() -> Void)?
    var onPresentation: (() -> Void)?
    var onInactive: (() -> Void)?

    init(link: PairingLink, profile: Data) { self.link = link; self.profile = profile }
    func start() { queue.async { [self] in active = true; attempt() } }
    func stop() { queue.async { [self] in active = false; connectionID = UUID(); releaseConnection(); onReset?() } }

    func updateProfile(_ data: Data) {
        queue.async { [self] in
            guard profile != data else { return }
            profile = data
            try? transport?.send(.profile, payload: data)
        }
    }

    func sendMouse(_ input: MouseInput) {
        queue.async { [self] in
            guard active, let data = try? JSONEncoder().encode(input) else { return }
            try? transport?.send(.input, payload: data)
        }
    }

    func presented(_ frame: DecodedFrame, time: Double) {
        queue.async { [self] in
            guard active, frame.connectionID == connectionID,
                  let report = presentation.presented(id: frame.id, time: time, width: frame.width, height: frame.height, codec: frame.codec),
                  report.sequence <= receivedFrames, let data = try? JSONEncoder().encode(report) else { return }
            do { try transport?.send(.presented, payload: data) } catch { return }
            onPresentation?()
            retry = 0
            onStatus?("\(frame.codec) · \(frame.width) × \(frame.height) · 实际呈现 \(String(format: "%.1f", report.fps)) fps", true)
        }
    }

    private func attempt() {
        guard active else { return }
        connectionID = UUID(); let id = connectionID
        receivedFrames = 0; presentation = PresentationMeter()
        onReset?(); onStatus?("正在连接 \(link.endpointDescription)…", false)
        let next = NativeTransport(link: link, queue: queue)
        transport = next
        next.onReady = { [weak self] in self?.onStatus?("加密连接已建立，等待电脑准备画面…", false) }
        next.onPacket = { [weak self] type, payload, done in
            guard let self, self.active, self.connectionID == id else { return }
            self.consume(type, payload: payload, connectionID: id, done: done)
        }
        next.onFailure = { [weak self] message, terminal in self?.failed(message, terminal: terminal, id: id) }
        next.start(initialProfile: profile)
    }

    private func consume(_ type: PacketType, payload: Data, connectionID id: UUID, done: @escaping () -> Void) {
        do {
            switch type {
            case .videoConfiguration:
                let configuration = try VideoConfiguration.parse(payload)
                let old = decoder; decoder = nil
                let configure = { [weak self] in
                    guard let self else { return }
                    self.queue.async {
                        guard self.active, self.connectionID == id else { return }
                        let next = H264Decoder(configuration: configuration, connectionID: id)
                        self.decoder = next
                        next.onFrame = { [weak self] frame in
                            guard let self else { return }
                            self.queue.async { if self.active && self.connectionID == id { self.onFrame?(frame) } }
                        }
                        next.onFailure = { [weak self] message in
                            guard let self else { return }
                            self.queue.async { self.failed(message, terminal: false, id: id) }
                        }
                        next.configure { [weak self] result in
                            guard let self else { return }
                            self.queue.async {
                                guard self.active, self.connectionID == id else { return }
                                switch result { case .success: done()
                                case .failure: self.failed("无法初始化 H.264 硬件解码，请降低电脑输出规格后重连。", terminal: true, id: id) }
                            }
                        }
                    }
                }
                if let old { old.close(completion: configure) } else { configure() }
            case .videoFrame:
                guard let decoder else { throw TabLinkError.invalid("视频配置必须先于帧数据") }
                let unit = try VideoAccessUnit(payload); receivedFrames += 1
                decoder.decode(unit, id: receivedFrames) { [weak self] result in
                    guard let self else { return }
                    self.queue.async {
                        guard self.active, self.connectionID == id else { return }
                        switch result { case .success: done()
                        case .failure: self.failed("视频解码中断，将重新建立会话。", terminal: false, id: id) }
                    }
                }
            case .jpeg:
                receivedFrames += 1
                onFrame?(try JPEGDecoder.decode(payload, id: receivedFrames, connectionID: id)); done()
            case .status:
                guard payload.count <= 16384 else { throw TabLinkError.invalid("状态包过大") }
                let status = try JSONDecoder().decode(ServerStatus.self, from: payload)
                let message = String((status.message ?? "已连接").prefix(180))
                onStatus?(status.capturePaused == true ? "采集暂停 · \(message)" : message, presentation.count > 0); done()
            case .error:
                // Do not echo arbitrary remote error content or credentials into logs/UI.
                failed("电脑拒绝或结束了会话。请查看电脑端状态，重新扫描当前二维码。", terminal: true, id: id)
            default: throw TabLinkError.invalid("不支持的服务端消息")
            }
        } catch { failed("收到无效或不兼容的画面数据，请检查电脑端版本。", terminal: true, id: id) }
    }

    private func failed(_ message: String, terminal: Bool, id: UUID) {
        guard active, connectionID == id else { return }
        connectionID = UUID(); releaseConnection(); onReset?(); onStatus?(message, false)
        if terminal { active = false; onInactive?(); return }
        let delay = min(10, 1 << min(retry, 4)); retry = min(5, retry + 1)
        let expected = connectionID
        queue.asyncAfter(deadline: .now() + .seconds(delay)) { [weak self] in
            guard let self, self.active, self.connectionID == expected else { return }
            self.attempt()
        }
    }

    private func releaseConnection() {
        transport?.cancel(); transport = nil
        decoder?.close(); decoder = nil
    }
}
