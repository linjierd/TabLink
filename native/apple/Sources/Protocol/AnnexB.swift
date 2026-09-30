import Foundation

enum AnnexB {
    static func units(_ data: Data) throws -> [Data] {
        let bytes = Array(data)
        func prefix(_ offset: Int) -> Int {
            guard offset + 2 < bytes.count, bytes[offset] == 0, bytes[offset + 1] == 0 else { return 0 }
            if bytes[offset + 2] == 1 { return 3 }
            return offset + 3 < bytes.count && bytes[offset + 2] == 0 && bytes[offset + 3] == 1 ? 4 : 0
        }
        guard prefix(0) != 0 else { throw TabLinkError.invalid("H.264 数据缺少 Annex-B 起始码") }
        var result: [Data] = []
        var start = prefix(0), position = start
        while position < bytes.count {
            let length = prefix(position)
            if length > 0 {
                guard position > start else { throw TabLinkError.invalid("空 H.264 单元") }
                result.append(Data(bytes[start..<position]))
                start = position + length; position = start
            } else { position += 1 }
        }
        guard start < bytes.count else { throw TabLinkError.invalid("截断的 H.264 单元") }
        result.append(Data(bytes[start..<bytes.count]))
        guard result.count <= 4096, result.allSatisfy({ unit in
            guard let header = unit.first else { return false }
            return header & 0x80 == 0 && (1...23).contains(header & 31)
        }) else { throw TabLinkError.invalid("无效的 H.264 单元头") }
        return result
    }
}

struct VideoAccessUnit {
    let ptsMicroseconds: Int64
    let avcc: Data
    let isKeyFrame: Bool

    init(_ payload: Data) throws {
        guard payload.count >= 13, payload.count <= PacketHeader.maxPayload else {
            throw TabLinkError.invalid("视频帧长度无效")
        }
        let timestamp = payload.prefix(8).reduce(UInt64(0)) { ($0 << 8) | UInt64($1) }
        guard timestamp <= UInt64(Int64.max) else { throw TabLinkError.invalid("视频时间戳无效") }
        let units = try AnnexB.units(Data(payload.dropFirst(8)))
        guard units.contains(where: { $0.first! & 31 == 1 || $0.first! & 31 == 5 }) else {
            throw TabLinkError.invalid("视频包中没有图像")
        }
        var avcc = Data(capacity: payload.count)
        for unit in units {
            let size = UInt32(unit.count)
            avcc.append(contentsOf: [UInt8((size >> 24) & 255), UInt8((size >> 16) & 255), UInt8((size >> 8) & 255), UInt8(size & 255)])
            avcc.append(unit)
        }
        ptsMicroseconds = Int64(timestamp); self.avcc = avcc
        isKeyFrame = units.contains(where: { $0.first! & 31 == 5 })
    }
}
