import Foundation

enum PacketType: UInt8 { case jpeg = 0x01, status = 0x02, error = 0x03, hello = 0x10,
    input = 0x11, presented = 0x12, profile = 0x13, videoConfiguration = 0x20, videoFrame = 0x21 }

struct PacketHeader {
    static let maxPayload = 8 * 1024 * 1024
    let type: PacketType
    let length: Int
    init(_ header: Data) throws {
        let bytes = Array(header)
        guard bytes.count == 5, let type = PacketType(rawValue: bytes[0]) else {
            throw TabLinkError.invalid("数据包头无效或协议不兼容")
        }
        let length = bytes[1...4].reduce(UInt32(0)) { ($0 << 8) | UInt32($1) }
        guard length > 0, length <= UInt32(Self.maxPayload) else { throw TabLinkError.invalid("数据包大小超过限制") }
        self.type = type; self.length = Int(length)
    }

    static func encode(_ type: PacketType, payload: Data) throws -> Data {
        guard !payload.isEmpty, payload.count <= maxPayload else { throw TabLinkError.invalid("发送包大小无效") }
        let length = UInt32(payload.count)
        var result = Data([type.rawValue, UInt8((length >> 24) & 255), UInt8((length >> 16) & 255),
                           UInt8((length >> 8) & 255), UInt8(length & 255)])
        result.append(payload); return result
    }
}

struct ClientHello: Encodable { let `protocol` = 1; let token: String }
struct ServerStatus: Decodable { let message: String?; let capturePaused: Bool? }
struct MouseInput: Encodable { let kind: String; let x: Double; let y: Double }

struct VideoConfiguration: Decodable {
    let codec: String
    let width: Int
    let height: Int
    let fps: Double
    let csd0: String
    let csd1: String

    static func parse(_ data: Data) throws -> VideoConfiguration {
        guard data.count <= 131072 else { throw TabLinkError.invalid("视频配置过大") }
        let value = try JSONDecoder().decode(Self.self, from: data)
        guard value.codec == "video/avc", value.width > 0, value.height > 0,
              value.width <= 8192, value.height <= 8192, value.width * value.height <= 16_000_000,
              value.fps.isFinite, (1...240).contains(value.fps) else { throw TabLinkError.invalid("视频尺寸或编码无效") }
        _ = try value.parameterSets()
        return value
    }

    func parameterSets() throws -> (Data, Data) {
        func parameter(_ encoded: String, type: UInt8) throws -> Data {
            guard let data = Data(base64Encoded: encoded), data.count <= 65536 else {
                throw TabLinkError.invalid("H.264 参数集无效")
            }
            let units = try AnnexB.units(data)
            guard units.count == 1, let first = units[0].first, first & 31 == type else {
                throw TabLinkError.invalid("H.264 参数集类型不匹配")
            }
            return units[0]
        }
        return (try parameter(csd0, type: 7), try parameter(csd1, type: 8))
    }
}
