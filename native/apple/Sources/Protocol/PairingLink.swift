import Foundation

enum TabLinkError: Error, LocalizedError {
    case invalid(String)
    var errorDescription: String? { if case .invalid(let text) = self { return text }; return nil }
}

/// Secrets deliberately have no debugDescription and are never inserted in errors or logs.
struct PairingLink {
    static let nativePorts: Set<UInt16> = [27184, 27186, 27187, 27188, 27189, 27190, 27191, 27192]
    let host: String
    let port: UInt16
    let token: String
    let certificatePin: Data
    private let rawURI: String

    init(_ text: String) throws {
        guard text.utf8.count <= 512, text == text.trimmingCharacters(in: .whitespacesAndNewlines),
              text.hasPrefix("tablink://connect?"), let url = URLComponents(string: text),
              url.scheme == "tablink", url.host == "connect", url.user == nil, url.password == nil,
              url.port == nil, url.path.isEmpty, url.fragment == nil, let query = url.percentEncodedQuery else {
            throw TabLinkError.invalid("请使用电脑端生成的 TabLink 原生客户端连接码")
        }
        var values: [String: String] = [:]
        for part in query.split(separator: "&", omittingEmptySubsequences: false) {
            let pieces = part.split(separator: "=", omittingEmptySubsequences: false)
            guard pieces.count == 2, ["host", "port", "token", "cert"].contains(String(pieces[0])),
                  values[String(pieces[0])] == nil else { throw TabLinkError.invalid("配对参数无效或重复") }
            values[String(pieces[0])] = String(pieces[1])
        }
        guard values.count == 4, let host = values["host"], Self.validIPv4(host),
              let portText = values["port"], let port = UInt16(portText), String(port) == portText,
              Self.nativePorts.contains(port) else {
            throw TabLinkError.invalid("电脑地址或原生会话端口无效；27185 是浏览器专用端口")
        }
        guard let token = values["token"], token.utf8.count == 64,
              token.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }),
              let pinText = values["cert"], let pin = Self.hex(pinText), pin.count == 32 else {
            throw TabLinkError.invalid("配对凭证或证书指纹无效，请重新扫码")
        }
        self.host = host; self.port = port; self.token = token; certificatePin = pin; rawURI = text
    }

    // Only explicit UI sharing / private storage may use this property.
    var privateURI: String { rawURI }
    var endpointDescription: String { "\(host):\(port)" }

    private static func validIPv4(_ text: String) -> Bool {
        let pieces = text.split(separator: ".", omittingEmptySubsequences: false)
        guard pieces.count == 4 else { return false }
        var numbers: [UInt8] = []
        for piece in pieces {
            guard !piece.isEmpty, piece.utf8.allSatisfy({ (48...57).contains($0) }),
                  let value = UInt8(piece), String(value) == String(piece) else { return false }
            numbers.append(value)
        }
        return numbers[0] > 0 && numbers[0] != 127 && numbers[0] < 224
    }

    static func hex(_ text: String) -> Data? {
        let bytes = Array(text.utf8)
        guard !bytes.isEmpty, bytes.count % 2 == 0 else { return nil }
        func nibble(_ byte: UInt8) -> UInt8? {
            switch byte { case 48...57: return byte - 48; case 65...70: return byte - 55
            case 97...102: return byte - 87; default: return nil }
        }
        var result = Data(capacity: bytes.count / 2)
        for index in stride(from: 0, to: bytes.count, by: 2) {
            guard let high = nibble(bytes[index]), let low = nibble(bytes[index + 1]) else { return nil }
            result.append((high << 4) | low)
        }
        return result
    }

    static func constantTimeEqual(_ left: Data, _ right: Data) -> Bool {
        guard left.count == right.count else { return false }
        return zip(left, right).reduce(UInt8(0)) { $0 | ($1.0 ^ $1.1) } == 0
    }
}
