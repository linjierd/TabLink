import Foundation
import Network
import Security
import CryptoKit

/// All state and callbacks belong to the supplied serial queue. There is no plaintext path.
final class NativeTransport {
    private let link: PairingLink
    private let queue: DispatchQueue
    private var connection: NWConnection?
    private var timer: DispatchSourceTimer?
    private var lastActivity = ProcessInfo.processInfo.systemUptime
    private var ended = false
    private var pinRejected = false
    private var pending: [(Data, (() -> Void)?)] = []
    private var sending = false
    var onReady: (() -> Void)?
    var onPacket: ((PacketType, Data, @escaping () -> Void) -> Void)?
    var onFailure: ((String, Bool) -> Void)?

    init(link: PairingLink, queue: DispatchQueue) { self.link = link; self.queue = queue }

    func start(initialProfile: Data) {
        let tls = NWProtocolTLS.Options()
        sec_protocol_options_set_min_tls_protocol_version(tls.securityProtocolOptions, .TLSv12)
        sec_protocol_options_set_max_tls_protocol_version(tls.securityProtocolOptions, .TLSv13)
        let expectedPin = link.certificatePin
        sec_protocol_options_set_verify_block(tls.securityProtocolOptions, { [weak self] _, suppliedTrust, complete in
            guard let self else { complete(false); return }
            let trust = sec_trust_copy_ref(suppliedTrust).takeRetainedValue()
            guard let certificates = SecTrustCopyCertificateChain(trust) as? [SecCertificate], let leaf = certificates.first else {
                self.pinRejected = true; complete(false); return
            }
            let actualPin = Data(SHA256.hash(data: SecCertificateCopyData(leaf) as Data))
            guard PairingLink.constantTimeEqual(expectedPin, actualPin) else {
                self.pinRejected = true; complete(false); return
            }
            // The QR supplies the trust anchor. A public CA or a matching hostname
            // never substitutes for the exact leaf certificate above.
            let anchors = [leaf] as CFArray
            let anchorsStatus = SecTrustSetAnchorCertificates(trust, anchors)
            let onlyStatus = SecTrustSetAnchorCertificatesOnly(trust, true)
            let policyStatus = SecTrustSetPolicies(trust, SecPolicyCreateBasicX509())
            let valid = anchorsStatus == errSecSuccess && onlyStatus == errSecSuccess
                && policyStatus == errSecSuccess && SecTrustEvaluateWithError(trust, nil)
            if !valid { self.pinRejected = true }
            complete(valid)
        }, queue)
        let tcp = NWProtocolTCP.Options()
        tcp.noDelay = true
        let parameters = NWParameters(tls: tls, tcp: tcp)
        let connection = NWConnection(host: NWEndpoint.Host(link.host), port: NWEndpoint.Port(rawValue: link.port)!, using: parameters)
        self.connection = connection
        connection.stateUpdateHandler = { [weak self] state in
            guard let self, !self.ended else { return }
            switch state {
            case .ready:
                self.lastActivity = ProcessInfo.processInfo.systemUptime
                do {
                    let hello = try JSONEncoder().encode(ClientHello(token: self.link.token))
                    try self.send(.hello, payload: hello)
                    try self.send(.profile, payload: initialProfile) { [weak self] in
                        self?.onReady?(); self?.readHeader()
                    }
                } catch { self.fail("无法创建认证请求", terminal: true) }
            case .failed:
                self.fail(self.pinRejected ? "电脑证书不匹配或已过期，请检查日期并重新扫描当前二维码。" : "网络连接失败，请检查同一 Wi-Fi、电脑会话和本地网络权限。",
                          terminal: self.pinRejected)
            case .cancelled:
                if !self.ended { self.fail("连接已关闭", terminal: false) }
            default: break
            }
        }
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 1, repeating: 1)
        timer.setEventHandler { [weak self] in
            guard let self, !self.ended else { return }
            if ProcessInfo.processInfo.systemUptime - self.lastActivity > 15 {
                self.fail("电脑长时间没有响应，将重新连接。", terminal: false)
            }
        }
        self.timer = timer; timer.resume()
        connection.start(queue: queue)
    }

    func send(_ type: PacketType, payload: Data, completion: (() -> Void)? = nil) throws {
        guard !ended else { throw TabLinkError.invalid("连接已经关闭") }
        guard pending.count < 64 else {
            fail("发送队列已满，重新连接以释放鼠标状态。", terminal: false)
            throw TabLinkError.invalid("发送队列已满")
        }
        pending.append((try PacketHeader.encode(type, payload: payload), completion))
        pumpSend()
    }

    private func pumpSend() {
        guard !ended, !sending, !pending.isEmpty, let connection else { return }
        sending = true
        let item = pending.removeFirst()
        connection.send(content: item.0, completion: .contentProcessed { [weak self] error in
            guard let self, !self.ended else { return }
            self.sending = false
            if error != nil { self.fail("发送失败，正在重新连接。", terminal: false); return }
            item.1?(); self.pumpSend()
        })
    }

    private func readHeader() {
        receiveExactly(5) { [weak self] headerData in
            guard let self else { return }
            do {
                let header = try PacketHeader(headerData)
                self.receiveExactly(header.length) { [weak self] payload in
                    guard let self else { return }
                    self.onPacket?(header.type, payload, { [weak self] in
                        guard let self else { return }
                        self.queue.async { [weak self] in self?.readHeader() }
                    })
                }
            } catch { self.fail("电脑发送了无效或不兼容的数据包。", terminal: true) }
        }
    }

    private func receiveExactly(_ count: Int, completion: @escaping (Data) -> Void) {
        guard !ended else { return }
        connection?.receive(minimumIncompleteLength: count, maximumLength: count) { [weak self] data, _, complete, error in
            guard let self, !self.ended else { return }
            guard error == nil, let data, data.count == count else {
                self.fail(complete ? "电脑结束了当前连接，将重新协商显示参数。" : "网络数据中断，将重新连接。", terminal: false)
                return
            }
            self.lastActivity = ProcessInfo.processInfo.systemUptime
            completion(data)
        }
    }

    func cancel() {
        ended = true; timer?.cancel(); timer = nil
        connection?.stateUpdateHandler = nil; connection?.cancel(); connection = nil
        pending.removeAll(); sending = false
        onReady = nil; onPacket = nil; onFailure = nil
    }

    private func fail(_ message: String, terminal: Bool) {
        guard !ended else { return }
        let callback = onFailure
        cancel(); callback?(message, terminal)
    }
}
