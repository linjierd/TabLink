import XCTest
@testable import TabLinkProtocol

final class ProtocolTests: XCTestCase {
    private let token = String(repeating: "a", count: 64)
    private let pin = String(repeating: "b", count: 64)
    private func uri(_ port: String = "27184", host: String = "192.168.1.5") -> String {
        "tablink://connect?host=\(host)&port=\(port)&token=\(token)&cert=\(pin)"
    }
    func testAllowedPortsAndCanonicalIPv4() throws {
        for port in [27184, 27186, 27187, 27188, 27189, 27190, 27191, 27192] {
            let link = try PairingLink(uri(String(port)))
            XCTAssertEqual(Int(link.port), port); XCTAssertEqual(link.certificatePin.count, 32)
        }
        for port in ["0", "1", "27183", "27185", "27193", "65535", "65536", "-1", "027184", "+27184", "27184.0", " 27184"] {
            XCTAssertThrowsError(try PairingLink(uri(port)))
        }
        for host in ["localhost", "::1", "127.0.0.1", "0.1.2.3", "224.0.0.1", "255.255.255.255", "192.168.001.5", "192.168.1", "256.0.0.1", "192.168.1.5%20"] {
            XCTAssertThrowsError(try PairingLink(uri(host: host)))
        }
    }
    func testCredentialsAndURIShape() throws {
        let text = uri()
        for invalid in [text + "&host=192.168.1.6", text + "&extra=1", text + "#fragment", text + "&",
                        text.replacingOccurrences(of: "connect?", with: "connect/?"),
                        text.replacingOccurrences(of: "connect?", with: "user@connect?"),
                        text.replacingOccurrences(of: token, with: token.uppercased()),
                        text.replacingOccurrences(of: token, with: String(token.dropLast())),
                        text.replacingOccurrences(of: pin, with: String(repeating: "z", count: 64)), " " + text] {
            XCTAssertThrowsError(try PairingLink(invalid))
        }
        XCTAssertNoThrow(try PairingLink(text.replacingOccurrences(of: pin, with: pin.uppercased())))
        XCTAssertTrue(PairingLink.constantTimeEqual(Data([0, 1]), Data([0, 1])))
        XCTAssertFalse(PairingLink.constantTimeEqual(Data([0, 1]), Data([0, 2])))
        XCTAssertFalse(PairingLink.constantTimeEqual(Data([0, 1]), Data([0])))
    }
    func testPacketBoundsAndHello() throws {
        let hello = try JSONEncoder().encode(ClientHello(token: token))
        let packet = try PacketHeader.encode(.hello, payload: hello)
        let header = try PacketHeader(Data(packet.prefix(5)))
        XCTAssertEqual(header.type, .hello); XCTAssertEqual(header.length, hello.count)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: hello) as? [String: Any])
        XCTAssertEqual(object["protocol"] as? Int, 1); XCTAssertEqual(object["token"] as? String, token)
        for bytes in [[0x21, 0, 0, 0, 0], [0x21, 0, 128, 0, 1], [0xff, 0, 0, 0, 1], [0x21, 0]] as [[UInt8]] {
            XCTAssertThrowsError(try PacketHeader(Data(bytes)))
        }
        XCTAssertEqual(try PacketHeader(Data([0x21, 0, 128, 0, 0])).length, 8 * 1024 * 1024)
        XCTAssertThrowsError(try PacketHeader.encode(.input, payload: Data()))
    }
    func testAnnexBAndAVCC() throws {
        let payload = Data([0, 0, 0, 0, 0, 0, 3, 232, 0, 0, 0, 1, 0x65, 0x88, 0, 0, 1, 0x41, 0x99])
        let unit = try VideoAccessUnit(payload)
        XCTAssertEqual(unit.ptsMicroseconds, 1000); XCTAssertTrue(unit.isKeyFrame)
        XCTAssertEqual(unit.avcc, Data([0, 0, 0, 2, 0x65, 0x88, 0, 0, 0, 2, 0x41, 0x99]))
        for bytes in [[0, 0, 0, 1], [0, 0, 1, 0x65, 0, 0, 1], [0, 0, 1, 0x80], [1, 2, 3], [0, 0, 1, 0]] as [[UInt8]] {
            XCTAssertThrowsError(try AnnexB.units(Data(bytes)))
        }
        var negative = payload; negative[0] = 0x80; XCTAssertThrowsError(try VideoAccessUnit(negative))
        XCTAssertThrowsError(try VideoAccessUnit(Data(repeating: 0, count: 8) + Data([0, 0, 0, 1, 0x67])))
    }
    func testFixturesAndConfiguration() throws {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "protocol", withExtension: "json", subdirectory: "Fixtures"))
        let fixture = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: Any])
        let config = try JSONSerialization.data(withJSONObject: try XCTUnwrap(fixture["videoConfiguration"]))
        XCTAssertEqual(try VideoConfiguration.parse(config).width, 640)
        let payload = try XCTUnwrap(PairingLink.hex(try XCTUnwrap(fixture["accessUnitHex"] as? String)))
        let expected = try XCTUnwrap(PairingLink.hex(try XCTUnwrap(fixture["avccHex"] as? String)))
        XCTAssertEqual(try VideoAccessUnit(payload).avcc, expected)
        var invalid = try XCTUnwrap(fixture["videoConfiguration"] as? [String: Any]); invalid["width"] = 8193
        XCTAssertThrowsError(try VideoConfiguration.parse(JSONSerialization.data(withJSONObject: invalid)))
        invalid["width"] = 640; invalid["codec"] = "video/hevc"
        XCTAssertThrowsError(try VideoConfiguration.parse(JSONSerialization.data(withJSONObject: invalid)))
    }
    func testPresentationCountsActualUniqueDrawablesOnly() {
        var meter = PresentationMeter()
        XCTAssertNil(meter.presented(id: 1, time: 0, width: 640, height: 480)); XCTAssertEqual(meter.count, 0)
        XCTAssertEqual(meter.presented(id: 1, time: 10, width: 640, height: 480)?.sequence, 1)
        XCTAssertNil(meter.presented(id: 1, time: 11, width: 640, height: 480))
        XCTAssertNil(meter.presented(id: 2, time: .nan, width: 640, height: 480))
        XCTAssertNil(meter.presented(id: 3, time: 10.5, width: 640, height: 480))
        XCTAssertEqual(meter.count, 2) // Frame 2 was not presented; IDs are not the ACK counter.
        let report = meter.presented(id: 4, time: 11, width: 640, height: 480)
        XCTAssertEqual(report?.sequence, 3); XCTAssertEqual(report?.fps, 2)
        XCTAssertNil(meter.presented(id: 2, time: 12, width: 640, height: 480)); XCTAssertEqual(meter.count, 3)
    }
    func testPinnedStableManifestAndTamperRejection() throws {
        let url = try XCTUnwrap(Bundle.module.url(forResource: "stable-manifest-valid", withExtension: "json", subdirectory: "Fixtures"))
        let envelope = try Data(contentsOf: url)
        let now = try XCTUnwrap(ISO8601DateFormatter().date(from: "2026-09-29T15:00:00Z"))
        let manifest = try StableUpdateManifest.verifyEnvelope(envelope, now: now)
        XCTAssertEqual(manifest.releaseID, "fixture-0.8.0")
        XCTAssertEqual(manifest.artifact(for: "android")?.version.description, "0.8.0")
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: envelope) as? [String: Any])
        var payload = try XCTUnwrap(Data(base64Encoded: try XCTUnwrap(object["payload"] as? String)))
        let text = try XCTUnwrap(String(data: payload, encoding: .utf8)).replacingOccurrences(of: "\"version\":\"0.8.0\"", with: "\"version\":\"0.8.1\"")
        payload = try XCTUnwrap(text.data(using: .utf8)); object["payload"] = payload.base64EncodedString()
        XCTAssertThrowsError(try StableUpdateManifest.verifyEnvelope(JSONSerialization.data(withJSONObject: object), now: now))
    }
    func testStableUpdatePolicyAndStrictJSON() throws {
        XCTAssertTrue(try StableSemanticVersion("0.8.0") > StableSemanticVersion("0.7.3"))
        for value in ["0.8", "0.8.0-beta", "00.8.0", "0.8.0+1"] { XCTAssertThrowsError(try StableSemanticVersion(value)) }
        let duplicate = Data("{\"schema\":1,\"schema\":1}".utf8)
        XCTAssertThrowsError(try StableUpdateManifest.parseVerifiedPayload(duplicate))
        let cohort = StableUpdateManifest.includesCohort("0123456789abcdef0123456789abcdef", releaseID: "fixture-0.8.0", percentage: 37)
        XCTAssertEqual(cohort, StableUpdateManifest.includesCohort("0123456789abcdef0123456789abcdef", releaseID: "fixture-0.8.0", percentage: 37))
        XCTAssertFalse(StableUpdateManifest.includesCohort("0123456789abcdef0123456789abcdef", releaseID: "fixture-0.8.0", percentage: 0))
        XCTAssertFalse(StableUpdateManifest.includesCohort("0123456789abcdef0123456789abcdef", releaseID: "stable-0.8.0", percentage: 20))
        XCTAssertTrue(StableUpdateManifest.includesCohort("0123456789abcdef0123456789abcdef", releaseID: "stable-0.8.0", percentage: 21))

        let fixtureURL = try XCTUnwrap(Bundle.module.url(forResource: "stable-manifest-valid", withExtension: "json", subdirectory: "Fixtures"))
        let envelope = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: fixtureURL)) as? [String: Any])
        let payload = try XCTUnwrap(Data(base64Encoded: try XCTUnwrap(envelope["payload"] as? String)))
        let now = try XCTUnwrap(ISO8601DateFormatter().date(from: "2026-09-29T15:00:00Z"))
        var root = try XCTUnwrap(JSONSerialization.jsonObject(with: payload) as? [String: Any])
        var artifacts = try XCTUnwrap(root["artifacts"] as? [[String: Any]])
        artifacts[0]["url"] = "https://linjie.space/download/api/download?path=TabLink%2Fstable%2FTabLink.ipa"
        root["artifacts"] = artifacts
        XCTAssertNoThrow(try StableUpdateManifest.parseVerifiedPayload(JSONSerialization.data(withJSONObject: root), now: now))
        for invalid in ["https://user@linjie.space/download/api/download?path=TabLink%2Fstable%2FTabLink.ipa",
                        "https://linjie.space/download/api/download?path=TabLink%2Fstable%2FTabLink.ipa#unsafe"] {
            artifacts[0]["url"] = invalid; root["artifacts"] = artifacts
            XCTAssertThrowsError(try StableUpdateManifest.parseVerifiedPayload(JSONSerialization.data(withJSONObject: root), now: now))
        }
        artifacts[0]["url"] = "https://linjie.space/download/api/download?path=TabLink%2Fstable%2FTabLink.ipa"
        root["artifacts"] = [artifacts[0], artifacts[0]]
        XCTAssertThrowsError(try StableUpdateManifest.parseVerifiedPayload(JSONSerialization.data(withJSONObject: root), now: now))
        root["artifacts"] = [artifacts[0]]
        for invalid in ["2026-09-29T14:35:00+00:00", "2026-09-29T14:35:00.1Z", "2026-02-31T14:35:00Z", "2026-09-29T24:00:00Z"] {
            root["publishedAtUtc"] = invalid
            XCTAssertThrowsError(try StableUpdateManifest.parseVerifiedPayload(JSONSerialization.data(withJSONObject: root), now: now))
        }
        root["publishedAtUtc"] = "2026-09-29T14:35:00Z"; root["rolloutPercentage"] = 0
        XCTAssertEqual(try StableUpdateManifest.parseVerifiedPayload(JSONSerialization.data(withJSONObject: root), now: now).rolloutPercentage, 0)
        for invalid in [-1, 101] {
            root["rolloutPercentage"] = invalid
            XCTAssertThrowsError(try StableUpdateManifest.parseVerifiedPayload(JSONSerialization.data(withJSONObject: root), now: now))
        }
        for valid in ["https://apps.apple.com/cn/app/tablink/id123456789",
                      "https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fios"] {
            XCTAssertTrue(StableUpdateManifest.isAllowedIOSInstallerURL(try XCTUnwrap(URL(string: valid))))
        }
        for invalid in ["http://apps.apple.com/cn/app/tablink/id123456789",
                        "https://user@apps.apple.com/cn/app/tablink/id123456789",
                        "https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fios#unsafe",
                        "https://linjie.space/download/api/download?path=Other%2Fios",
                        "https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fios&extra=1",
                        "https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fios&path=TabLink%2Fstable%2Fother"] {
            XCTAssertFalse(StableUpdateManifest.isAllowedIOSInstallerURL(try XCTUnwrap(URL(string: invalid))))
        }
        let ios = StableReleaseArtifact(platform: "ios", version: try StableSemanticVersion("0.9.0"), build: 3,
            url: try XCTUnwrap(URL(string: "https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fios")),
            size: 123, sha256: String(repeating: "A", count: 64),
            installerURL: try XCTUnwrap(URL(string: "https://apps.apple.com/cn/app/tablink/id123456789")), notes: nil)
        let iosManifest = StableReleaseManifest(releaseID: "stable-0.9.0", publishedAt: now, rolloutPercentage: 100,
            minimumProtocolVersion: 1, artifacts: [ios])
        XCTAssertNotNil(try StableUpdateManifest.availableIOSUpdate(iosManifest, currentVersion: "0.8.0", currentBuild: 2,
            cohortID: "0123456789abcdef0123456789abcdef"))
        XCTAssertNil(try StableUpdateManifest.availableIOSUpdate(iosManifest, currentVersion: "0.8.0", currentBuild: 3,
            cohortID: "0123456789abcdef0123456789abcdef"))
    }
}
