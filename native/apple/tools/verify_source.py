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

def main():
    fixture = json.loads((ROOT / "Tests/Protocol/Fixtures/protocol.json").read_text(encoding="utf-8"))
    info = plistlib.loads((ROOT / "Info.plist").read_bytes())
    check(info["CFBundleURLTypes"][0]["CFBundleURLSchemes"] == ["tablink"], "URL scheme")
    check(bool(info["NSLocalNetworkUsageDescription"]) and bool(info["NSCameraUsageDescription"]), "permission descriptions")
    check(info["UIApplicationSceneManifest"]["UIApplicationSupportsMultipleScenes"] is False, "one active scene")
    project_path = ROOT / "TabLink.xcodeproj/project.pbxproj"
    before = project_path.read_bytes()
    subprocess.run([sys.executable, str(ROOT / "tools/generate_project.py")], check=True, capture_output=True)
    check(project_path.read_bytes() == before, "deterministic up-to-date project")
    project = before.decode("utf-8")
    check("MARKETING_VERSION = 0.8.0" in project and "CURRENT_PROJECT_VERSION = 2" in project, "Apple version metadata 0.8.0 build 2")
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
    public_key = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEXlJPucXJJzRwjf1p/46Uuebom2dMFvSSiN4wdwxVVtbb9bdaIGnru39akKRRd7BaTlUaEk2Thmb/MpqNPYNu4A=="
    manifest_url = "https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json"
    check(public_key in stable and "P256.Signing.PublicKey(derRepresentation: spki)" in stable, "pinned P-256 SPKI verifier")
    check("ECDSASignature(derRepresentation: signatureData)" in stable and "key.isValidSignature" in stable, "DER ECDSA signature verification")
    check("平台无效或重复" in stable and '== "stable"' in stable and "StrictJSONScanner.validate" in stable, "strict stable manifest policy")
    check(manifest_url in manager and "apps.apple.com" in stable and "isAllowedIOSInstallerURL" in manager, "official manifest, App Store and DownloadSite allowlist")
    check("UIApplication.shared.open" in manager and "URLSession.shared.bytes" in manager, "bounded background check and App Store handoff")
    check("StableUpdateManager.shared.start()" in delegate and "sceneDidBecomeActive" in delegate and "scheduledTimer" in manager, "launch foreground periodic checks")
    check("updateStatus" in main and "updateButton" in main and "UIAlert" not in manager + main, "nonblocking connection-panel update state")
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
