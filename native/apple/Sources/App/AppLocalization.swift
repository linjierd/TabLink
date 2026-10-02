import Foundation

enum AppLanguage: String, CaseIterable {
    case system
    case simplifiedChinese = "zh-Hans"
    case english = "en"

    static func decode(_ value: Any?) -> AppLanguage {
        guard let raw = value as? String, let language = AppLanguage(rawValue: raw) else { return .system }
        return language
    }

    var title: String {
        switch self {
        case .system: return L10n.text("language.system")
        case .simplifiedChinese: return L10n.text("language.zhHans")
        case .english: return L10n.text("language.english")
        }
    }
}

enum L10n {
    static let languageChanged = Notification.Name("TabLinkLanguageChanged")
    private static let preferenceKey = "appLanguageV1"

    static var language: AppLanguage {
        AppLanguage.decode(UserDefaults.standard.object(forKey: preferenceKey))
    }

    static func setLanguage(_ language: AppLanguage) {
        guard language != self.language else { return }
        UserDefaults.standard.set(language.rawValue, forKey: preferenceKey)
        NotificationCenter.default.post(name: languageChanged, object: nil)
    }

    private static var effectiveLanguage: String {
        switch language {
        case .simplifiedChinese: return "zh-Hans"
        case .english: return "en"
        case .system:
            let systemLanguage = Locale.preferredLanguages.first?.lowercased() ?? "en"
            return systemLanguage.hasPrefix("zh") ? "zh-Hans" : "en"
        }
    }

    private static var localizedBundle: Bundle {
        let language = effectiveLanguage
        guard let path = Bundle.main.path(forResource: language, ofType: "lproj"),
              let bundle = Bundle(path: path) else { return .main }
        return bundle
    }

    static var locale: Locale { Locale(identifier: effectiveLanguage) }
    static var isSimplifiedChinese: Bool { effectiveLanguage == "zh-Hans" }

    static func text(_ key: String, _ arguments: CVarArg...) -> String {
        let format = localizedBundle.localizedString(forKey: key, value: nil, table: nil)
        guard !arguments.isEmpty else { return format }
        return String(format: format, locale: locale, arguments: arguments)
    }
}

enum HostStatusText {
    private static let knownMessages: [String: String] = [
        "Windows 桌面暂不可采集，等待返回普通桌面。": "session.captureUnavailable",
        "正在恢复同一虚拟副屏画面。": "session.captureRecovering",
        "画面采集暂时不可用，正在等待恢复。": "session.captureUnavailable",
        "USB 副屏画面已恢复。": "session.captureResumed"
    ]

    static func localize(_ value: String?, capturePaused: Bool) -> String {
        let normalized = String((value ?? "").replacingOccurrences(
            of: #"[\p{C}]"#, with: " ", options: .regularExpression).trimmingCharacters(in: .whitespaces).prefix(180))
        if let key = knownMessages[normalized] { return L10n.text(key) }
        guard !normalized.isEmpty else {
            return L10n.text(capturePaused ? "session.capturePausedGeneric" : "session.hostStatusGeneric")
        }
        let containsHan = normalized.unicodeScalars.contains { scalar in
            (0x3400...0x4DBF).contains(scalar.value) || (0x4E00...0x9FFF).contains(scalar.value) ||
                (0xF900...0xFAFF).contains(scalar.value) || (0x20000...0x323AF).contains(scalar.value)
        }
        guard containsHan == L10n.isSimplifiedChinese else {
            return L10n.text(capturePaused ? "session.capturePausedGeneric" : "session.hostStatusGeneric")
        }
        return capturePaused ? L10n.text("session.capturePaused", normalized) : normalized
    }
}
