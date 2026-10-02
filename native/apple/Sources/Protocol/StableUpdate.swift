import Foundation
import CryptoKit
import CoreFoundation

enum StableUpdateError: Error, Equatable {
    case invalid(String)
}

enum StableUpdateMode: String, CaseIterable, Equatable {
    case automatic
    case downloadThenAsk
    case never

    var title: String {
        switch self {
        case .automatic: return "自动更新"
        case .downloadThenAsk: return "自动下载后手动安装"
        case .never: return "从不更新"
        }
    }

    // A missing preference is the first-run default. Any present value that is not
    // one of the exact known strings fails closed so it cannot silently enable traffic.
    static func decodePersisted(_ value: Any?) -> StableUpdateMode {
        guard let value else { return .automatic }
        guard let raw = value as? String, let mode = StableUpdateMode(rawValue: raw) else { return .never }
        return mode
    }
}

struct StableSemanticVersion: Comparable, Equatable, CustomStringConvertible {
    let major: UInt64
    let minor: UInt64
    let patch: UInt64

    init(_ text: String) throws {
        let parts = text.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { throw StableUpdateError.invalid("正式版本号必须是 major.minor.patch") }
        func part(_ value: Substring) throws -> UInt64 {
            guard !value.isEmpty, value.allSatisfy({ $0.isASCII && $0.isNumber }),
                  value.count == 1 || value.first != "0", let number = UInt64(value) else {
                throw StableUpdateError.invalid("正式版本号格式无效")
            }
            return number
        }
        major = try part(parts[0]); minor = try part(parts[1]); patch = try part(parts[2])
    }

    static func < (left: Self, right: Self) -> Bool {
        if left.major != right.major { return left.major < right.major }
        if left.minor != right.minor { return left.minor < right.minor }
        return left.patch < right.patch
    }
    var description: String { "\(major).\(minor).\(patch)" }
}

struct StableReleaseArtifact: Equatable {
    let platform: String
    let version: StableSemanticVersion
    let build: Int
    let url: URL
    let size: Int64
    let sha256: String
    let installerURL: URL?
    let notes: String?
}

struct StableReleaseManifest: Equatable {
    let releaseID: String
    let publishedAt: Date
    let rolloutPercentage: Int
    let minimumProtocolVersion: Int
    let artifacts: [StableReleaseArtifact]

    func artifact(for platform: String) -> StableReleaseArtifact? { artifacts.first { $0.platform == platform } }
}

struct StableUpdateFloor: Equatable {
    let publishedAt: Date
    let releaseID: String
    let fingerprint: String
    let blocked: Bool

    init(publishedAt: Date, releaseID: String, fingerprint: String, blocked: Bool = false) {
        self.publishedAt = publishedAt
        self.releaseID = releaseID
        self.fingerprint = fingerprint
        self.blocked = blocked
    }
}

enum StableUpdateManifest {
    static let currentProtocolVersion = 1
    static let signerSPKIBase64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXlJPucXJJzRwjf1p/46Uuebom2dMFvSSiN4wdwxVVtbb9bdaIGnru39akKRRd7BaTlUaEk2Thmb/MpqNPYNu4A=="

    static func verifyEnvelope(_ envelopeData: Data, now: Date = Date()) throws -> StableReleaseManifest {
        guard envelopeData.count > 0, envelopeData.count <= 384 * 1024 else { throw StableUpdateError.invalid("更新清单大小无效") }
        try StrictJSONScanner.validate(envelopeData)
        let envelope = try object(JSONSerialization.jsonObject(with: envelopeData), "签名清单")
        try exactKeys(envelope, required: ["payload", "signature"])
        let payload = try canonicalBase64(string(envelope["payload"], "payload"), "payload")
        let signatureData = try canonicalBase64(string(envelope["signature"], "signature"), "signature")
        guard payload.count > 0, payload.count <= 256 * 1024 else { throw StableUpdateError.invalid("更新负载大小无效") }
        guard let spki = Data(base64Encoded: signerSPKIBase64), spki.base64EncodedString() == signerSPKIBase64 else {
            throw StableUpdateError.invalid("内置更新公钥无效")
        }
        do {
            let key = try P256.Signing.PublicKey(derRepresentation: spki)
            let signature = try P256.Signing.ECDSASignature(derRepresentation: signatureData)
            guard key.isValidSignature(signature, for: payload) else { throw StableUpdateError.invalid("更新清单签名验证失败") }
        } catch let error as StableUpdateError { throw error }
        catch { throw StableUpdateError.invalid("更新清单签名格式无效") }
        return try parseVerifiedPayload(payload, now: now)
    }

    static func parseVerifiedPayload(_ payload: Data, now: Date = Date()) throws -> StableReleaseManifest {
        try StrictJSONScanner.validate(payload)
        let root = try object(JSONSerialization.jsonObject(with: payload), "更新负载")
        try exactKeys(root, required: ["schema", "channel", "releaseId", "publishedAtUtc", "rolloutPercentage", "minimumProtocolVersion", "artifacts"])
        guard try integer(root["schema"], "schema") == 1 else { throw StableUpdateError.invalid("不支持的更新清单版本") }
        guard try string(root["channel"], "channel") == "stable" else { throw StableUpdateError.invalid("只接受 stable 正式版") }
        let releaseID = try string(root["releaseId"], "releaseId")
        guard matches(releaseID, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$") else { throw StableUpdateError.invalid("releaseId 无效") }
        let publishedText = try string(root["publishedAtUtc"], "publishedAtUtc")
        let published = try parseTimestamp(publishedText)
        guard published <= now.addingTimeInterval(24 * 3600), published >= now.addingTimeInterval(-366 * 24 * 3600) else {
            throw StableUpdateError.invalid("更新清单时间异常或已经过期")
        }
        let rollout = try integer(root["rolloutPercentage"], "rolloutPercentage")
        guard (0...100).contains(rollout) else { throw StableUpdateError.invalid("发布比例无效") }
        let minimumProtocol = try integer(root["minimumProtocolVersion"], "minimumProtocolVersion")
        guard minimumProtocol >= 1 else { throw StableUpdateError.invalid("最低协议版本无效") }
        guard let rawArtifacts = root["artifacts"] as? [Any], !rawArtifacts.isEmpty, rawArtifacts.count <= 32 else {
            throw StableUpdateError.invalid("更新清单没有有效安装包")
        }
        var platforms = Set<String>()
        var artifacts: [StableReleaseArtifact] = []
        for value in rawArtifacts {
            let item = try object(value, "artifact")
            try exactKeys(item, required: ["platform", "version", "build", "url", "size", "sha256"], optional: ["installerUrl", "notes"])
            let platform = try string(item["platform"], "platform")
            guard ["windows-x64", "android", "ios", "harmony"].contains(platform), platforms.insert(platform).inserted else {
                throw StableUpdateError.invalid("平台无效或重复")
            }
            let version = try StableSemanticVersion(string(item["version"], "version"))
            let build = try integer(item["build"], "build")
            guard build >= 1 else { throw StableUpdateError.invalid("构建号无效") }
            let url = try httpsURL(string(item["url"], "url"), "url")
            let size = try integer64(item["size"], "size")
            guard size > 0, size <= 8 * 1024 * 1024 * 1024 else { throw StableUpdateError.invalid("安装包大小无效") }
            let hash = try string(item["sha256"], "sha256")
            guard matches(hash, "^[0-9A-Fa-f]{64}$") else { throw StableUpdateError.invalid("SHA-256 无效") }
            let installer = try item["installerUrl"].map { try httpsURL(try string($0, "installerUrl"), "installerUrl") }
            let notes = try item["notes"].map { try string($0, "notes") }
            guard notes?.count ?? 0 <= 4096 else { throw StableUpdateError.invalid("更新说明过长") }
            artifacts.append(StableReleaseArtifact(platform: platform, version: version, build: build, url: url,
                size: size, sha256: hash.uppercased(), installerURL: installer, notes: notes))
        }
        return StableReleaseManifest(releaseID: releaseID, publishedAt: published, rolloutPercentage: rollout,
            minimumProtocolVersion: minimumProtocol, artifacts: artifacts)
    }

    static func includesCohort(_ cohortID: String, releaseID: String, percentage: Int) -> Bool {
        if percentage >= 100 { return true }
        if percentage <= 0 { return false }
        let digest = SHA256.hash(data: Data((cohortID + "\n" + releaseID).utf8))
        let bytes = Array(digest)
        let value = (UInt32(bytes[0]) << 24) | (UInt32(bytes[1]) << 16) | (UInt32(bytes[2]) << 8) | UInt32(bytes[3])
        return Int(value % 100) < percentage
    }

    static func availableIOSUpdate(_ manifest: StableReleaseManifest, currentVersion: String, currentBuild: Int, cohortID: String) throws -> StableReleaseArtifact? {
        guard manifest.minimumProtocolVersion <= currentProtocolVersion,
              includesCohort(cohortID, releaseID: manifest.releaseID, percentage: manifest.rolloutPercentage),
              let artifact = manifest.artifact(for: "ios"),
              artifact.build > currentBuild,
              artifact.version > (try StableSemanticVersion(currentVersion)) else { return nil }
        return artifact
    }

    static func selectNewest(_ manifests: [StableReleaseManifest], floor: StableUpdateFloor? = nil) throws -> StableReleaseManifest {
        guard let newestDate = manifests.map(\.publishedAt).max() else {
            throw StableUpdateError.invalid("没有可用的已签名更新清单")
        }
        let newest = manifests.filter { $0.publishedAt == newestDate }
        guard let selected = newest.first else { throw StableUpdateError.invalid("没有可用的已签名更新清单") }
        if try newestConflictPublishedAt(manifests) != nil {
            throw StableUpdateError.invalid("同一发布时间的更新清单内容冲突")
        }
        if let floor {
            guard floor.publishedAt.timeIntervalSince1970.isFinite,
                  floor.publishedAt.timeIntervalSince1970.rounded() == floor.publishedAt.timeIntervalSince1970 else {
                throw StableUpdateError.invalid("本地更新防回退记录损坏")
            }
            if floor.blocked {
                guard floor.releaseID == "__conflict__", floor.fingerprint == String(repeating: "0", count: 64) else {
                    throw StableUpdateError.invalid("本地更新冲突阻断记录损坏")
                }
                guard selected.publishedAt > floor.publishedAt else {
                    throw StableUpdateError.invalid("更新清单未晚于已记录的签名冲突")
                }
                return selected
            }
            guard matches(floor.releaseID, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$"),
                  matches(floor.fingerprint, "^[0-9A-F]{64}$") else {
                throw StableUpdateError.invalid("本地更新防回退记录损坏")
            }
            guard selected.publishedAt >= floor.publishedAt else {
                throw StableUpdateError.invalid("更新清单早于已经接受的正式版决定")
            }
            if selected.publishedAt == floor.publishedAt {
                guard selected.releaseID == floor.releaseID,
                      try decisionFingerprint(selected) == floor.fingerprint else {
                    throw StableUpdateError.invalid("更新清单与已经接受的同一时刻决定冲突")
                }
            }
        }
        return selected
    }

    static func newestConflictPublishedAt(_ manifests: [StableReleaseManifest]) throws -> Date? {
        guard let newestDate = manifests.map(\.publishedAt).max() else { return nil }
        let newest = manifests.filter { $0.publishedAt == newestDate }
        guard let first = newest.first else { return nil }
        let fingerprint = try decisionFingerprint(first)
        for candidate in newest.dropFirst() {
            if try decisionFingerprint(candidate) != fingerprint { return newestDate }
        }
        return nil
    }

    static func floor(for manifest: StableReleaseManifest) throws -> StableUpdateFloor {
        StableUpdateFloor(publishedAt: manifest.publishedAt, releaseID: manifest.releaseID,
                          fingerprint: try decisionFingerprint(manifest), blocked: false)
    }

    static func conflictFloor(at publishedAt: Date) -> StableUpdateFloor {
        StableUpdateFloor(publishedAt: publishedAt, releaseID: "__conflict__",
                          fingerprint: String(repeating: "0", count: 64), blocked: true)
    }

    static func decisionFingerprint(_ manifest: StableReleaseManifest) throws -> String {
        let artifacts: [[String: Any]] = manifest.artifacts.sorted { $0.platform < $1.platform }.map { artifact in
            ["platform": artifact.platform, "version": artifact.version.description, "build": artifact.build,
             // The package URL is a transport mirror. Size and SHA-256 identify
             // the package, while installerUrl and notes remain release semantics.
             "size": artifact.size, "sha256": artifact.sha256,
             "installerUrl": (artifact.installerURL?.absoluteString as Any?) ?? NSNull(),
             "notes": (artifact.notes as Any?) ?? NSNull()]
        }
        let record: [String: Any] = [
            "releaseId": manifest.releaseID,
            "publishedAtEpochSeconds": Int64(manifest.publishedAt.timeIntervalSince1970.rounded()),
            "rolloutPercentage": manifest.rolloutPercentage,
            "minimumProtocolVersion": manifest.minimumProtocolVersion,
            "artifacts": artifacts
        ]
        let data = try JSONSerialization.data(withJSONObject: record, options: [.sortedKeys])
        return SHA256.hash(data: data).map { String(format: "%02X", $0) }.joined()
    }

    static func isAllowedIOSInstallerURL(_ url: URL) -> Bool {
        guard let components = URLComponents(url: url, resolvingAgainstBaseURL: false),
              components.scheme?.lowercased() == "https", components.user == nil, components.password == nil,
              components.fragment == nil, let host = components.host?.lowercased() else { return false }
        if host == "apps.apple.com" { return true }
        guard host == "linjie.space", components.path == "/download/api/download",
              let items = components.queryItems, items.count == 1, items[0].name == "path",
              let path = items[0].value else { return false }
        return path.hasPrefix("TabLink/stable/")
    }

    private static func parseTimestamp(_ text: String) throws -> Date {
        let expression = try NSRegularExpression(pattern: "^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})Z$")
        let whole = NSRange(text.startIndex..<text.endIndex, in: text)
        guard let match = expression.firstMatch(in: text, range: whole) else {
            throw StableUpdateError.invalid("发布时间必须是整秒 UTC RFC3339")
        }
        func number(_ group: Int) -> Int? {
            guard match.range(at: group).location != NSNotFound, let range = Range(match.range(at: group), in: text) else { return nil }
            return Int(text[range])
        }
        guard let year = number(1), let month = number(2), let day = number(3), let hour = number(4),
              let minute = number(5), let second = number(6), (0...23).contains(hour),
              (0...59).contains(minute), (0...59).contains(second) else { throw StableUpdateError.invalid("发布时间无效") }
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = TimeZone(secondsFromGMT: 0)!
        let components = DateComponents(calendar: calendar, timeZone: calendar.timeZone, year: year, month: month,
                                        day: day, hour: hour, minute: minute, second: second)
        guard let base = calendar.date(from: components) else { throw StableUpdateError.invalid("发布时间无效") }
        let check = calendar.dateComponents([.year, .month, .day, .hour, .minute, .second], from: base)
        guard check.year == year, check.month == month, check.day == day, check.hour == hour,
              check.minute == minute, check.second == second else { throw StableUpdateError.invalid("发布时间无效") }
        return base
    }

    private static func httpsURL(_ text: String, _ name: String) throws -> URL {
        guard let components = URLComponents(string: text), components.scheme?.lowercased() == "https",
              components.host?.isEmpty == false, components.user == nil, components.password == nil,
              components.fragment == nil, let url = components.url else {
            throw StableUpdateError.invalid("\(name) 必须是无凭据和片段的 HTTPS 地址")
        }
        return url
    }

    private static func canonicalBase64(_ text: String, _ name: String) throws -> Data {
        guard !text.isEmpty, text.rangeOfCharacter(from: .whitespacesAndNewlines) == nil,
              let value = Data(base64Encoded: text), value.base64EncodedString() == text else {
            throw StableUpdateError.invalid("\(name) 不是规范 Base64")
        }
        return value
    }

    private static func object(_ value: Any, _ name: String) throws -> [String: Any] {
        guard let result = value as? [String: Any] else { throw StableUpdateError.invalid("\(name) 必须是对象") }
        return result
    }
    private static func string(_ value: Any?, _ name: String) throws -> String {
        guard let result = value as? String, !result.isEmpty else { throw StableUpdateError.invalid("\(name) 必须是非空字符串") }
        return result
    }
    private static func integer(_ value: Any?, _ name: String) throws -> Int {
        let result = try integer64(value, name)
        guard result >= Int64(Int.min), result <= Int64(Int.max) else { throw StableUpdateError.invalid("\(name) 超出范围") }
        return Int(result)
    }
    private static func integer64(_ value: Any?, _ name: String) throws -> Int64 {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID() else { throw StableUpdateError.invalid("\(name) 必须是整数") }
        let double = number.doubleValue
        guard double.isFinite, double.rounded() == double, double >= Double(Int64.min), double <= Double(Int64.max) else {
            throw StableUpdateError.invalid("\(name) 必须是整数")
        }
        return number.int64Value
    }
    private static func exactKeys(_ object: [String: Any], required: Set<String>, optional: Set<String> = []) throws {
        let keys = Set(object.keys)
        guard required.isSubset(of: keys), keys.isSubset(of: required.union(optional)) else { throw StableUpdateError.invalid("JSON 字段缺失或未知") }
    }
    private static func matches(_ text: String, _ pattern: String) -> Bool {
        text.range(of: pattern, options: .regularExpression) != nil
    }
}

private struct StrictJSONScanner {
    private let bytes: [UInt8]
    private var index = 0
    private var depth = 0

    static func validate(_ data: Data) throws {
        var scanner = StrictJSONScanner(bytes: Array(data))
        try scanner.value(); scanner.whitespace()
        guard scanner.index == scanner.bytes.count else { throw StableUpdateError.invalid("JSON 包含尾随数据") }
    }

    private mutating func value() throws {
        whitespace(); guard index < bytes.count else { throw StableUpdateError.invalid("JSON 意外结束") }
        switch bytes[index] {
        case 0x7b: try object()
        case 0x5b: try array()
        case 0x22: _ = try string()
        default:
            let start = index
            while index < bytes.count, ![0x2c, 0x5d, 0x7d, 0x20, 0x09, 0x0a, 0x0d].contains(bytes[index]) { index += 1 }
            guard index > start else { throw StableUpdateError.invalid("JSON 值无效") }
        }
    }

    private mutating func object() throws {
        try enter(); defer { depth -= 1 }; index += 1; whitespace()
        if consume(0x7d) { return }
        var keys = Set<String>()
        while true {
            whitespace(); let key = try string()
            guard keys.insert(key).inserted else { throw StableUpdateError.invalid("JSON 包含重复字段") }
            whitespace(); guard consume(0x3a) else { throw StableUpdateError.invalid("JSON 对象缺少冒号") }
            try value(); whitespace()
            if consume(0x7d) { return }
            guard consume(0x2c) else { throw StableUpdateError.invalid("JSON 对象分隔符无效") }
        }
    }

    private mutating func array() throws {
        try enter(); defer { depth -= 1 }; index += 1; whitespace()
        if consume(0x5d) { return }
        while true {
            try value(); whitespace()
            if consume(0x5d) { return }
            guard consume(0x2c) else { throw StableUpdateError.invalid("JSON 数组分隔符无效") }
        }
    }

    private mutating func string() throws -> String {
        guard consume(0x22) else { throw StableUpdateError.invalid("JSON 字段名必须是字符串") }
        let start = index - 1
        while index < bytes.count {
            let byte = bytes[index]; index += 1
            if byte == 0x22 {
                let data = Data(bytes[start..<index])
                guard let decoded = try JSONSerialization.jsonObject(with: data, options: .fragmentsAllowed) as? String else {
                    throw StableUpdateError.invalid("JSON 字符串无效")
                }
                return decoded
            }
            if byte == 0x5c {
                guard index < bytes.count else { throw StableUpdateError.invalid("JSON 转义无效") }
                let escaped = bytes[index]; index += 1
                if escaped == 0x75 {
                    guard index + 4 <= bytes.count, bytes[index..<index + 4].allSatisfy({ ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x41 && $0 <= 0x46) || ($0 >= 0x61 && $0 <= 0x66) }) else {
                        throw StableUpdateError.invalid("JSON Unicode 转义无效")
                    }
                    index += 4
                }
            }
        }
        throw StableUpdateError.invalid("JSON 字符串没有结束")
    }

    private mutating func enter() throws { depth += 1; if depth > 32 { throw StableUpdateError.invalid("JSON 嵌套过深") } }
    private mutating func whitespace() { while index < bytes.count, [0x20, 0x09, 0x0a, 0x0d].contains(bytes[index]) { index += 1 } }
    private mutating func consume(_ byte: UInt8) -> Bool { guard index < bytes.count, bytes[index] == byte else { return false }; index += 1; return true }
}
