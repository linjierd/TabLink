import Foundation
import UIKit

enum StableUpdateState: Equatable {
    case checking
    case current(String)
    case notPublished
    case available(version: String, installerURL: URL)
    case unavailable

    var text: String {
        switch self {
        case .checking: return "正式版更新：正在安全检查…"
        case .current(let version): return "正式版更新：当前 \(version) 已是最新"
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
    static let manifestURL = URL(string: "https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json")!
    private static let checkInterval: TimeInterval = 6 * 60 * 60
    private let defaults = UserDefaults.standard
    private var task: Task<Void, Never>?
    private var timer: Timer?
    private(set) var state: StableUpdateState = .checking {
        didSet { if oldValue != state { NotificationCenter.default.post(name: Self.stateChanged, object: self) } }
    }

    private init() {}

    func start() {
        if timer == nil {
            timer = Timer.scheduledTimer(withTimeInterval: 60 * 60, repeats: true) { _ in
                Task { @MainActor in StableUpdateManager.shared.checkIfNeeded() }
            }
        }
        checkIfNeeded(force: true)
    }

    func checkIfNeeded(force: Bool = false) {
        guard task == nil else { return }
        if !force, let last = defaults.object(forKey: "stableUpdateLastSuccess") as? Date,
           Date().timeIntervalSince(last) < Self.checkInterval { return }
        task = Task { [weak self] in
            guard let self else { return }
            await self.runCheck()
        }
    }

    func openInstaller() {
        guard let url = state.installerURL else { return }
        UIApplication.shared.open(url, options: [:]) { [weak self] opened in
            if !opened { self?.state = .unavailable }
        }
    }

    private func runCheck() async {
        defer { task = nil }
        do {
            var request = URLRequest(url: Self.manifestURL, cachePolicy: .reloadIgnoringLocalAndRemoteCacheData, timeoutInterval: 30)
            request.httpMethod = "GET"; request.setValue("application/json", forHTTPHeaderField: "Accept")
            let (bytes, response) = try await URLSession.shared.bytes(for: request)
            guard let http = response as? HTTPURLResponse, http.statusCode == 200,
                  response.url?.scheme?.lowercased() == "https" else { throw StableUpdateError.invalid("更新服务器响应无效") }
            var envelope = Data(); envelope.reserveCapacity(min(max(0, Int(http.expectedContentLength)), 384 * 1024))
            for try await byte in bytes {
                guard envelope.count < 384 * 1024 else { throw StableUpdateError.invalid("更新清单过大") }
                envelope.append(byte)
            }
            let manifest = try StableUpdateManifest.verifyEnvelope(envelope)
            defaults.set(Date(), forKey: "stableUpdateLastSuccess")
            guard let artifact = manifest.artifact(for: "ios") else { state = .notPublished; return }
            let current = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.8.0"
            let currentBuild = Int(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "2") ?? 2
            guard let available = try StableUpdateManifest.availableIOSUpdate(manifest, currentVersion: current, currentBuild: currentBuild, cohortID: cohortID()),
                  available == artifact else { state = .current(current); return }
            guard let installer = artifact.installerURL, StableUpdateManifest.isAllowedIOSInstallerURL(installer) else {
                state = .unavailable; return
            }
            state = .available(version: artifact.version.description, installerURL: installer)
        } catch is CancellationError {
            return
        } catch {
            state = .unavailable
        }
    }

    private func cohortID() -> String {
        if let existing = defaults.string(forKey: "stableUpdateCohortID"),
           existing.range(of: "^[0-9a-f]{32}$", options: .regularExpression) != nil { return existing }
        let created = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        defaults.set(created, forKey: "stableUpdateCohortID")
        return created
    }

}
