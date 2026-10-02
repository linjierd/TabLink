"""Windows-capable artifact/fixture audit; deliberately NOT a Swift compilation test."""
from pathlib import Path
import base64
import hashlib
import json
import plistlib
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
checks = 0

def check(condition, message):
    global checks
    if not condition:
        raise AssertionError(message)
    checks += 1

def header(data):
    if len(data) != 5 or data[0] not in (1, 2, 3, 16, 17, 18, 19, 32, 33):
        raise ValueError("header")
    length = int.from_bytes(data[1:], "big")
    if not 0 < length <= 8 * 1024 * 1024:
        raise ValueError("size")
    return length

def annexb(data):
    starts = list(re.finditer(b"\x00\x00(?:\x00)?\x01", data))
    if not starts or starts[0].start() != 0:
        raise ValueError("start")
    nals = [data[m.end():starts[i + 1].start() if i + 1 < len(starts) else len(data)] for i, m in enumerate(starts)]
    if any(not n or n[0] & 128 or not 1 <= (n[0] & 31) <= 23 for n in nals):
        raise ValueError("nal")
    return nals

def rejects(function, value):
    try:
        function(value)
    except ValueError:
        return True
    return False

def localized_strings(path):
    text = path.read_text(encoding="utf-8")
    pairs = re.findall(r'^\s*"((?:[^"\\]|\\.)+)"\s*=\s*"((?:[^"\\]|\\.)*)"\s*;\s*$', text, re.MULTILINE)
    keys = [key for key, _ in pairs]
    check(len(keys) == len(set(keys)), f"duplicate localization key in {path.name}")
    check(len(pairs) > 0 and not re.sub(r'^\s*"(?:[^"\\]|\\.)+"\s*=\s*"(?:[^"\\]|\\.)*"\s*;\s*$', '', text,
                                        flags=re.MULTILINE).strip(), f"invalid strings syntax in {path.name}")
    return dict(pairs)

def main():
    fixture = json.loads((ROOT / "Tests/Protocol/Fixtures/protocol.json").read_text(encoding="utf-8"))
    info = plistlib.loads((ROOT / "Info.plist").read_bytes())
    check(info["CFBundleURLTypes"][0]["CFBundleURLSchemes"] == ["tablink"], "URL scheme")
    check(bool(info["NSLocalNetworkUsageDescription"]) and bool(info["NSCameraUsageDescription"]), "permission descriptions")
    check(info["UIApplicationSceneManifest"]["UIApplicationSupportsMultipleScenes"] is False, "one active scene")
    check(info["CFBundleDevelopmentRegion"] == "en", "unsupported Apple languages fall back to English")
    en_strings = localized_strings(ROOT / "Resources/en.lproj/Localizable.strings")
    zh_strings = localized_strings(ROOT / "Resources/zh-Hans.lproj/Localizable.strings")
    en_info = localized_strings(ROOT / "Resources/en.lproj/InfoPlist.strings")
    zh_info = localized_strings(ROOT / "Resources/zh-Hans.lproj/InfoPlist.strings")
    check(en_strings.keys() == zh_strings.keys(), "English and Simplified Chinese UI resource keys match")
    check(en_info.keys() == zh_info.keys(), "English and Simplified Chinese InfoPlist resource keys match")
    check(all(en_strings[key] and zh_strings[key] for key in en_strings), "localized UI values are nonempty")
    check(all(en_info[key] and zh_info[key] for key in en_info), "localized InfoPlist values are nonempty")
    placeholders = lambda value: re.findall(r"%(?:[0-9]+\$)?[@df]", value.replace("%%", ""))
    check(all(placeholders(en_strings[key]) == placeholders(zh_strings[key]) for key in en_strings),
          "English and Simplified Chinese format placeholders match")
    project_path = ROOT / "TabLink.xcodeproj/project.pbxproj"
    before = project_path.read_bytes()
    subprocess.run([sys.executable, str(ROOT / "tools/generate_project.py")], check=True, capture_output=True)
    check(project_path.read_bytes() == before, "deterministic up-to-date project")
    project = before.decode("utf-8")
    check("MARKETING_VERSION = 0.8.0" in project and "CURRENT_PROJECT_VERSION = 2" in project, "Apple version metadata 0.8.0 build 2")
    check("developmentRegion = en" in project and 'knownRegions = (en, "zh-Hans", Base,)' in project,
          "Xcode project declares English and Simplified Chinese regions")
    for resource in ("Localizable.strings", "InfoPlist.strings"):
        check(f"name = {resource};" in project and f"Resources/en.lproj/{resource}" in project and
              f"Resources/zh-Hans.lproj/{resource}" in project, f"localized {resource} is bundled")
    source_paths = sorted(str(p.relative_to(ROOT)).replace("\\", "/") for p in (ROOT / "Sources").rglob("*") if p.suffix in (".swift", ".metal"))
    for source in source_paths:
        check(f'path = "{source}";' in project, f"project missing {source}")
    check("IPHONEOS_DEPLOYMENT_TARGET = 17.0" in project and 'DEVELOPMENT_TEAM = ""' in project, "deployment and no signing identity")
    definitions = re.findall(r"^\s*([A-F0-9]{24}) = \{", project, re.MULTILINE)
    check(len(definitions) == len(set(definitions)), "unique PBX identifiers")
    for reference in set(re.findall(r"\b[A-F0-9]{24}\b", project)):
        check(reference in definitions, "dangling PBX identifier")
    scheme = ET.parse(ROOT / "TabLink.xcodeproj/xcshareddata/xcschemes/TabLink.xcscheme")
    for entry in scheme.findall(".//BuildableReference"):
        check(entry.attrib["BlueprintIdentifier"] in definitions, "scheme target exists")
    ports = {27184, 27186, 27187, 27188, 27189, 27190, 27191, 27192}
    pairing = (ROOT / "Sources/Protocol/PairingLink.swift").read_text(encoding="utf-8")
    match = re.search(r"nativePorts: Set<UInt16> = \[([^]]+)\]", pairing)
    check(match is not None and {int(p.strip()) for p in match[1].split(",")} == ports, "Swift port set")
    for port in fixture["allowedPorts"]:
        check(port in ports, "allowed fixture port")
    for port in fixture["rejectedPorts"]:
        check(port not in ports, "rejected fixture port")
    for encoded in fixture["validHeaders"]:
        check(header(bytes.fromhex(encoded)) > 0, "valid header fixture")
    for encoded in fixture["invalidHeaders"]:
        check(rejects(header, bytes.fromhex(encoded)), "invalid header fixture")
    for encoded in fixture["invalidAnnexBHex"]:
        check(rejects(annexb, bytes.fromhex(encoded)), "invalid Annex B fixture")
    unit = bytes.fromhex(fixture["accessUnitHex"])
    nals = annexb(unit[8:])
    avcc = b"".join(len(n).to_bytes(4, "big") + n for n in nals)
    check(avcc.hex() == fixture["avccHex"], "Annex B to AVCC fixture")
    check(int.from_bytes(unit[:8], "big") == fixture["ptsMicroseconds"], "PTS fixture")
    config = fixture["videoConfiguration"]
    for key, naltype in (("csd0", 7), ("csd1", 8)):
        units = annexb(base64.b64decode(config[key], validate=True))
        check(len(units) == 1 and units[0][0] & 31 == naltype, "parameter fixture")
    transport = (ROOT / "Sources/App/NativeTransport.swift").read_text(encoding="utf-8")
    check("SecCertificateCopyData(leaf)" in transport and "constantTimeEqual(expectedPin, actualPin)" in transport, "exact DER pin path exists")
    check("SecTrustEvaluateWithError" in transport and "NWParameters(tls: tls" in transport, "TLS trust evaluation path exists")
    presenter = (ROOT / "Sources/App/MetalPresenter.swift").read_text(encoding="utf-8")
    check("drawable.addPresentedHandler" in presenter and "presented.presentedTime > 0" in presenter, "presentation callback exists")
    check("self?.onPresented?(frame, presented.presentedTime)" in presenter, "ACK originates in presented callback")
    check("RequireHardwareAcceleratedVideoDecoder" in (ROOT / "Sources/App/H264Decoder.swift").read_text(encoding="utf-8"), "hardware decoder requested")
    stable = (ROOT / "Sources/Protocol/StableUpdate.swift").read_text(encoding="utf-8")
    manager = (ROOT / "Sources/App/StableUpdateManager.swift").read_text(encoding="utf-8")
    delegate = (ROOT / "Sources/App/AppDelegate.swift").read_text(encoding="utf-8")
    main = (ROOT / "Sources/App/MainViewController.swift").read_text(encoding="utf-8")
    protocol_tests = (ROOT / "Tests/Protocol/ProtocolTests.swift").read_text(encoding="utf-8")
    public_key = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXlJPucXJJzRwjf1p/46Uuebom2dMFvSSiN4wdwxVVtbb9bdaIGnru39akKRRd7BaTlUaEk2Thmb/MpqNPYNu4A=="
    manifest_url = "https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json"
    fallback_manifest_url = "https://github.com/linjierd/TabLink/releases/latest/download/manifest.json"
    check(public_key in stable and "P256.Signing.PublicKey(derRepresentation: spki)" in stable, "pinned P-256 SPKI verifier")
    check("ECDSASignature(derRepresentation: signatureData)" in stable and "key.isValidSignature" in stable, "DER ECDSA signature verification")
    check("平台无效或重复" in stable and '== "stable"' in stable and "StrictJSONScanner.validate" in stable, "strict stable manifest policy")
    check(manifest_url in manager and fallback_manifest_url in manager and "apps.apple.com" in stable and "isAllowedIOSInstallerURL" in manager,
          "blog and GitHub signed-manifest sources, App Store and DownloadSite allowlist")
    check("UIApplication.shared.open" in manager and "URLSession.shared.bytes" in manager, "bounded background check and App Store handoff")
    check("StableUpdateManager.shared.start()" in delegate and "sceneDidBecomeActive" in delegate and "scheduledTimer" in manager, "launch foreground periodic checks")
    check("updateStatus" in main and "updateButton" in main and "UIAlert" not in manager + main, "nonblocking connection-panel update state")
    check(all(key in en_strings for key in ("update.mode.automatic", "update.mode.downloadThenAsk", "update.mode.never")) and
          "decodePersisted" in stable and "return .never" in stable, "three localized persisted update modes and fail-closed decoding")
    check("guard updateMode != .never" in manager and "cancelActiveCheck()" in manager and
          "timer?.invalidate()" in manager, "never mode cancels work and gates update network checks")
    check("selectNewest(manifests" in manager and "decisionFingerprint" in stable and
          "stableUpdateAcceptedFloorV1" in manager and "同一发布时间" in stable,
          "newest signed decision, conflict rejection and persisted anti-replay floor")
    check("newestConflictPublishedAt" in stable and "persistConflictFloor" in manager and
          "runtimeConflictFloor" in manager and "conflictFloor" in stable and
          "更新清单未晚于已记录的签名冲突" in stable,
          "signed same-time conflicts persist a fail-closed floor until a newer decision")
    fingerprint_body = stable.split("static func decisionFingerprint", 1)[1].split("static func isAllowedIOSInstallerURL", 1)[0]
    check("manifest.artifacts.sorted" in fingerprint_body and '"url": artifact.url.absoluteString' not in fingerprint_body and
          all(field in fingerprint_body for field in ('"size"', '"sha256"', '"installerUrl"', '"notes"')),
          "canonical decision fingerprint sorts artifacts and excludes only package mirror URL")
    check(all(state in manager for state in (".unsupportedProtocol", ".paused", ".cohortDeferred")),
          "future protocol, pause and cohort states remain distinct")
    check("InstallerExpectation" in manager and "already displayed link" in manager and
          "expectation.fingerprint == acceptedFloor.fingerprint" in manager,
          "explicit App Store tap revalidates the current signed decision")
    check(all(name in protocol_tests for name in ("reorderedURLOnlyMirrors", "installerConflict", "rolloutConflict",
          "conflictFloor", "stable-after-conflict")),
          "semantic mirror equivalence and conflict XCTest fixtures exist")
    localization = (ROOT / "Sources/App/AppLocalization.swift").read_text(encoding="utf-8")
    native_session = (ROOT / "Sources/App/NativeSession.swift").read_text(encoding="utf-8")
    check("showsMenuAsPrimaryAction" in main and 'L10n.text("action.checkUpdate")' in main and
          "StableUpdateMode.allCases" in main, "localized in-app update-mode settings and explicit check action")
    check("AppLanguage: String, CaseIterable" in localization and "case system" in localization and
          'case simplifiedChinese = "zh-Hans"' in localization and 'case english = "en"' in localization and
          'preferenceKey = "appLanguageV1"' in localization and "Locale.preferredLanguages.first" in localization and
          'hasPrefix("zh") ? "zh-Hans" : "en"' in localization,
          "persisted system, Simplified Chinese and English language choices")
    check("enum HostStatusText" in localization and "knownMessages" in localization and
          "containsHan == L10n.isSimplifiedChinese" in localization and
          'L10n.text(capturePaused ? "session.capturePausedGeneric" : "session.hostStatusGeneric")' in localization and
          "HostStatusText.localize(status.message" in native_session and "refreshLocalizedHostStatus" in native_session and
          "session?.refreshLocalizedHostStatus()" in main,
          "host status uses known local mappings and hides language-mismatched free text without reconnecting")
    used_localization_keys = set(re.findall(r'L10n\.text\("([^"]+)"', '\n'.join(
        p.read_text(encoding="utf-8") for p in (ROOT / "Sources/App").glob("*.swift"))))
    check(used_localization_keys <= set(en_strings), "every Apple L10n key exists in both language resources")
    check({"language.system", "language.zhHans", "language.english", "settings.title",
           "action.scan", "action.paste", "scanner.unavailable", "status.invalidPairing",
           "session.connectingEndpoint", "session.hostRejected", "transport.pinRejected",
           "transport.networkFailed", "update.mode.automatic", "update.mode.downloadThenAsk",
           "update.mode.never", "update.available", "update.unavailable"} <= used_localization_keys,
          "language, settings, scan/link, certificate trust, connection errors and update states are localized")
    check(not re.search(r"replaceItem|moveItem|removeItem|\.ipa\b", manager, re.IGNORECASE), "no in-app package replacement")
    apple_update_fixture = json.loads((ROOT / "Tests/Protocol/Fixtures/stable-manifest-valid.json").read_text(encoding="utf-8"))
    shared_update_fixture = json.loads((ROOT.parents[1] / "android/tests/fixtures/stable-manifest-valid.json").read_text(encoding="utf-8"))
    check(apple_update_fixture == shared_update_fixture, "Apple and Android signed fixture interoperability")
    signed_payload = json.loads(base64.b64decode(apple_update_fixture["payload"], validate=True))
    check(signed_payload["schema"] == 1 and signed_payload["channel"] == "stable", "signed fixture schema and channel")
    check(base64.b64decode(apple_update_fixture["signature"], validate=True)[0] == 0x30, "signed fixture DER signature")
    print(f"PASS: {checks} project/plist/scheme/fixture/source-presence checks; {len(source_paths)} application sources.")
    print("NOT RUN: Swift compilation, XCTest execution, Xcode/App Store build/signing, CryptoKit/App Store runtime, VideoToolbox/Metal runtime, device/network tests.")
    digest = hashlib.sha256(before).hexdigest()
    print(f"project.pbxproj SHA256: {digest}")

if __name__ == "__main__":
    main()
