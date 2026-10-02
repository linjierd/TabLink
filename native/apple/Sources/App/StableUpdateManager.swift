import Foundation
import UIKit

enum StableUpdateState: Equatable {
    case disabled
    case checking
    case current(String)
    case paused
    case cohortDeferred
    case unsupportedProtocol(Int)
    case notPublished
    case available(version: String, installerURL: URL)
    case unavailable

    var text: String {
        switch self {
        case .disabled: return "正式版更新：已选择从不更新，不会联网检查"
        case .checking: return "正式版更新：正在安全检查…"
        case .current(let version): return "正式版更新：当前 \(version) 已是最新"
        case .paused: return "正式版更新：发布方已暂停本次更新"
        case .cohortDeferred: return "正式版更新：分批发布尚未覆盖此设备"
        case .unsupportedProtocol(let required): return "正式版更新：需要客户端支持更新协议 \(required)，当前版本无法安全处理"
        case .notPublished: return "正式版更新：iOS / iPadOS 版本尚未发布"
        case .available(let version, _): return "正式版更新：App Store 已有 \(version)"
        case .unavailable: return "正式版更新：暂时无法完成安全检查"
        }
    }
    var installerURL: URL? { if case .available(_, let url) = self { return url }; return nil }
}

@MainActor
final class StableUpdateManager {
    static let shared = StableUpdateManager()
    static let stateChanged = Notification.Name("TabLinkStableUpdateStateChanged")
    static let manifestURLs = [
        URL(string: "https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json")!,
        URL(string: "https://github.com/linjierd/TabLink/releases/latest/download/manifest.json")!
    ]
    private static let checkInterval: TimeInterval = 6 * 60 * 60
    private static let updateModeKey = "stableUpdateModeV1"
    private static let acceptedFloorKey = "stableUpdateAcceptedFloorV1"

    private struct StoredFloor: Codable {
        let schema: Int
        let publishedAtEpochSeconds: Int64
        let releaseID: String
        let fingerprint: String
        let blocked: Bool?
    }

    private struct InstallerExpectation {
        let fingerprint: String
        let installerURL: URL
    }

    private let defaults: UserDefaults
    private var task: Task<Void, Never>?
    private var timer: Timer?
    private var generation = 0
    private var availableDecisionFingerprint: String?
    private var runtimeConflictFloor: Date?
    private(set) var updateMode: StableUpdateMode
    private(set) var state: StableUpdateState {
        didSet { if oldValue != state { notifyChange() } }
    }

    private init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        updateMode = StableUpdateMode.decodePersisted(defaults.object(forKey: Self.updateModeKey))
        state = updateMode == .never ? .disabled : .checking
    }

    func start() {
        guard updateMode != .never else { state = .disabled; return }
        ensureTimer()
        checkIfNeeded(force: true)
    }

    func setUpdateMode(_ mode: StableUpdateMode) {
        guard mode != updateMode else { return }
        defaults.set(mode.rawValue, forKey: Self.updateModeKey)
        updateMode = mode
        cancelActiveCheck()
        availableDecisionFingerprint = nil
        if mode == .never {
            timer?.invalidate(); timer = nil
            state = .disabled
        } else {
            state = .checking
            ensureTimer()
            checkIfNeeded(force: true)
        }
        // The state can remain textually equal while the selected menu item and
        // explanatory text changed, so always publish one preference notification.
        notifyChange()
    }

    func checkNow() {
        guard updateMode != .never else { state = .disabled; return }
        checkIfNeeded(force: true)
    }

    func checkIfNeeded(force: Bool = false) {
        startCheck(force: force, installerExpectation: nil)
    }

    private func startCheck(force: Bool, installerExpectation: InstallerExpectation?) {
        guard updateMode != .never, task == nil else { return }
        if !force, let last = defaults.object(forKey: "stableUpdateLastSuccess") as? Date,
           Date().timeIntervalSince(last) < Self.checkInterval { return }
        availableDecisionFingerprint = nil
        state = .checking
        generation += 1
        let attempt = generation
        task = Task { [weak self] in
            guard let self else { return }
            await self.runCheck(generation: attempt, installerExpectation: installerExpectation)
        }
    }

    // This is only called by an explicit tap. Apple does not expose an API for
    // this app to silently download or install its own App Store update.
    func openInstaller() {
        guard updateMode != .never, let url = state.installerURL,
              let fingerprint = availableDecisionFingerprint else { return }
        // Re-read both signed sources before honoring the tap. This prevents an
        // already displayed link from bypassing a newer signed pause or conflict.
        cancelActiveCheck()
        startCheck(force: true, installerExpectation: InstallerExpectation(
            fingerprint: fingerprint, installerURL: url))
    }

    private func ensureTimer() {
        guard updateMode != .never, timer == nil else { return }
        timer = Timer.scheduledTimer(withTimeInterval: 60 * 60, repeats: true) { _ in
            Task { @MainActor in StableUpdateManager.shared.checkIfNeeded() }
        }
    }

    private func cancelActiveCheck() {
        generation += 1
        task?.cancel()
        task = nil
    }

    private func isCurrent(_ attempt: Int) -> Bool {
        attempt == generation && updateMode != .never && !Task.isCancelled
    }

    private func runCheck(generation attempt: Int, installerExpectation: InstallerExpectation?) async {
        defer { if attempt == generation { task = nil } }
        do {
            var manifests: [StableReleaseManifest] = []
            for url in Self.manifestURLs {
                guard isCurrent(attempt) else { throw CancellationError() }
                do {
                    let envelope = try await fetchEnvelope(from: url, generation: attempt)
                    manifests.append(try StableUpdateManifest.verifyEnvelope(envelope))
                } catch is CancellationError {
                    throw CancellationError()
                } catch {
                    // A broken mirror cannot supply a decision. Any other mirror
                    // still has to pass the same pinned signed-manifest validation.
                    continue
                }
            }
            guard isCurrent(attempt) else { throw CancellationError() }
            let storedFloor = try loadAcceptedFloor()
            let floor = effectiveFloor(storedFloor)
            if let conflict = try StableUpdateManifest.newestConflictPublishedAt(manifests) {
                try persistConflictFloor(at: conflict, existing: storedFloor)
                throw StableUpdateError.invalid("同一发布时间的已签名更新决定冲突；已阻断该时间及更早决定")
            }
            let manifest = try StableUpdateManifest.selectNewest(manifests, floor: floor)
            guard isCurrent(attempt) else { throw CancellationError() }
            let acceptedFloor = try StableUpdateManifest.floor(for: manifest)
            try persistAcceptedFloor(acceptedFloor)
            defaults.set(Date(), forKey: "stableUpdateLastSuccess")

            let current = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.8.0"
            let currentBuild = Int(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "2") ?? 2
            guard manifest.minimumProtocolVersion <= StableUpdateManifest.currentProtocolVersion else {
                state = .unsupportedProtocol(manifest.minimumProtocolVersion); return
            }
            guard manifest.rolloutPercentage > 0 else { state = .paused; return }
            guard let artifact = manifest.artifact(for: "ios") else { state = .notPublished; return }
            let cohort = cohortID()
            guard StableUpdateManifest.includesCohort(cohort, releaseID: manifest.releaseID,
                                                       percentage: manifest.rolloutPercentage) else {
                state = .cohortDeferred; return
            }
            guard let available = try StableUpdateManifest.availableIOSUpdate(manifest, currentVersion: current,
                    currentBuild: currentBuild, cohortID: cohort), available == artifact else {
                state = .current(current); return
            }
            guard let installer = artifact.installerURL, StableUpdateManifest.isAllowedIOSInstallerURL(installer) else {
                state = .unavailable; return
            }
            availableDecisionFingerprint = acceptedFloor.fingerprint
            state = .available(version: artifact.version.description, installerURL: installer)
            if let expectation = installerExpectation,
               expectation.fingerprint == acceptedFloor.fingerprint,
               expectation.installerURL == installer,
               isCurrent(attempt) {
                UIApplication.shared.open(installer, options: [:]) { [weak self] opened in
                    Task { @MainActor in
                        guard let self, self.isCurrent(attempt) else { return }
                        if !opened {
                            self.availableDecisionFingerprint = nil
                            self.state = .unavailable
                        }
                    }
                }
            }
        } catch is CancellationError {
            return
        } catch {
            if isCurrent(attempt) {
                availableDecisionFingerprint = nil
                state = .unavailable
            }
        }
    }

    private func fetchEnvelope(from url: URL, generation attempt: Int) async throws -> Data {
        guard isCurrent(attempt) else { throw CancellationError() }
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalAndRemoteCacheData, timeoutInterval: 30)
        request.httpMethod = "GET"
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        let (bytes, response) = try await URLSession.shared.bytes(for: request)
        guard isCurrent(attempt) else { throw CancellationError() }
        guard let http = response as? HTTPURLResponse, http.statusCode == 200,
              response.url?.scheme?.lowercased() == "https" else {
            throw StableUpdateError.invalid("更新服务器响应无效")
        }
        var envelope = Data()
        envelope.reserveCapacity(min(max(0, Int(http.expectedContentLength)), 384 * 1024))
        for try await byte in bytes {
            guard isCurrent(attempt) else { throw CancellationError() }
            guard envelope.count < 384 * 1024 else { throw StableUpdateError.invalid("更新清单过大") }
            envelope.append(byte)
        }
        return envelope
    }

    private func loadAcceptedFloor() throws -> StableUpdateFloor? {
        guard let stored = defaults.object(forKey: Self.acceptedFloorKey) else { return nil }
        guard let data = stored as? Data else { throw StableUpdateError.invalid("本地更新防回退记录损坏") }
        let record: StoredFloor
        do { record = try JSONDecoder().decode(StoredFloor.self, from: data) }
        catch { throw StableUpdateError.invalid("本地更新防回退记录损坏") }
        guard record.schema == 1 else { throw StableUpdateError.invalid("本地更新防回退记录版本无效") }
        // The signed manifest parser only accepts contemporary dates. A broad
        // civil-date bound also prevents an invalid Int64-to-Double round trip.
        guard (0...253_402_300_799).contains(record.publishedAtEpochSeconds) else {
            throw StableUpdateError.invalid("本地更新防回退记录时间无效")
        }
        let date = Date(timeIntervalSince1970: TimeInterval(record.publishedAtEpochSeconds))
        guard date.timeIntervalSince1970.isFinite else {
            throw StableUpdateError.invalid("本地更新防回退记录时间无效")
        }
        let blocked = record.blocked ?? false
        if blocked {
            guard record.releaseID == "__conflict__", record.fingerprint == String(repeating: "0", count: 64) else {
                throw StableUpdateError.invalid("本地更新冲突阻断记录损坏")
            }
        }
        return StableUpdateFloor(publishedAt: date, releaseID: record.releaseID,
                                 fingerprint: record.fingerprint, blocked: blocked)
    }

    private func persistAcceptedFloor(_ floor: StableUpdateFloor) throws {
        try writeFloor(floor)
        if let runtimeConflictFloor, floor.publishedAt > runtimeConflictFloor {
            self.runtimeConflictFloor = nil
        }
    }

    private func persistConflictFloor(at publishedAt: Date, existing: StableUpdateFloor?) throws {
        if let current = runtimeConflictFloor {
            if publishedAt > current { runtimeConflictFloor = publishedAt }
        } else {
            runtimeConflictFloor = publishedAt
        }
        if let existing, existing.publishedAt > publishedAt { return }
        var target = publishedAt
        if let existing, existing.blocked, existing.publishedAt >= publishedAt {
            target = existing.publishedAt
        }
        try writeFloor(StableUpdateManifest.conflictFloor(at: target))
    }

    private func effectiveFloor(_ stored: StableUpdateFloor?) -> StableUpdateFloor? {
        guard let runtimeConflictFloor else { return stored }
        guard let stored else { return StableUpdateManifest.conflictFloor(at: runtimeConflictFloor) }
        if runtimeConflictFloor >= stored.publishedAt {
            return StableUpdateManifest.conflictFloor(at: runtimeConflictFloor)
        }
        return stored
    }

    private func writeFloor(_ floor: StableUpdateFloor) throws {
        let seconds = floor.publishedAt.timeIntervalSince1970
        guard seconds.isFinite, seconds.rounded() == seconds,
              seconds >= Double(Int64.min), seconds <= Double(Int64.max) else {
            throw StableUpdateError.invalid("更新清单时间无法保存")
        }
        let record = StoredFloor(schema: 1, publishedAtEpochSeconds: Int64(seconds),
                                 releaseID: floor.releaseID, fingerprint: floor.fingerprint,
                                 blocked: floor.blocked)
        defaults.set(try JSONEncoder().encode(record), forKey: Self.acceptedFloorKey)
        guard defaults.synchronize() else {
            throw StableUpdateError.invalid("无法持久保存更新防回退记录")
        }
    }

    private func cohortID() -> String {
        if let existing = defaults.string(forKey: "stableUpdateCohortID"),
           existing.range(of: "^[0-9a-f]{32}$", options: .regularExpression) != nil { return existing }
        let created = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        defaults.set(created, forKey: "stableUpdateCohortID")
        return created
    }

    private func notifyChange() {
        NotificationCenter.default.post(name: Self.stateChanged, object: self)
    }
}
