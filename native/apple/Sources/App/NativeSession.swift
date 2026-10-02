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
    private var lastHostStatus: (message: String?, capturePaused: Bool, displaying: Bool)?
    var onStatus: ((String, Bool) -> Void)?
    var onFrame: ((DecodedFrame) -> Void)?
    var onReset: (() -> Void)?
    var onPresentation: (() -> Void)?
    var onInactive: (() -> Void)?

    init(link: PairingLink, profile: Data) { self.link = link; self.profile = profile }
    func start() { queue.async { [self] in active = true; attempt() } }
    func stop() { queue.async { [self] in active = false; connectionID = UUID(); releaseConnection(); onReset?() } }

    func refreshLocalizedHostStatus() {
        queue.async { [self] in
            guard active, let lastHostStatus else { return }
            onStatus?(HostStatusText.localize(lastHostStatus.message,
                                              capturePaused: lastHostStatus.capturePaused),
                      lastHostStatus.displaying)
        }
    }

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
            lastHostStatus = nil
            onStatus?(L10n.text("session.presenting", frame.codec, frame.width, frame.height, report.fps), true)
        }
    }

    private func attempt() {
        guard active else { return }
        connectionID = UUID(); let id = connectionID
        receivedFrames = 0; presentation = PresentationMeter()
        lastHostStatus = nil
        onReset?(); onStatus?(L10n.text("session.connectingEndpoint", link.endpointDescription), false)
        let next = NativeTransport(link: link, queue: queue)
        transport = next
        next.onReady = { [weak self] in
            self?.lastHostStatus = nil
            self?.onStatus?(L10n.text("session.secureReady"), false)
        }
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
                                case .failure: self.failed(L10n.text("session.decoderInitFailed"), terminal: true, id: id) }
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
                        case .failure: self.failed(L10n.text("session.decodeInterrupted"), terminal: false, id: id) }
                    }
                }
            case .jpeg:
                receivedFrames += 1
                onFrame?(try JPEGDecoder.decode(payload, id: receivedFrames, connectionID: id)); done()
            case .status:
                guard payload.count <= 16384 else { throw TabLinkError.invalid("状态包过大") }
                let status = try JSONDecoder().decode(ServerStatus.self, from: payload)
                lastHostStatus = (status.message, status.capturePaused == true, presentation.count > 0)
                onStatus?(HostStatusText.localize(status.message, capturePaused: status.capturePaused == true),
                          presentation.count > 0); done()
            case .error:
                // Do not echo arbitrary remote error content or credentials into logs/UI.
                failed(L10n.text("session.hostRejected"), terminal: true, id: id)
            default: throw TabLinkError.invalid("不支持的服务端消息")
            }
        } catch { failed(L10n.text("session.invalidData"), terminal: true, id: id) }
    }

    private func failed(_ message: String, terminal: Bool, id: UUID) {
        guard active, connectionID == id else { return }
        lastHostStatus = nil
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
