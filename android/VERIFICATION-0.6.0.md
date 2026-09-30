# Android 0.6.0 build verification — 2026-09-29

Artifact: `artifacts/TabLink-android-0.6.0-debug.apk` (360150 bytes)

SHA-256: `E789B8625846BC65C0D3DAA6603AEAE6A0337930E405B8AC2EB732D968DA0E07`

Package: `com.tablink.client`, versionCode 8, versionName 0.6.0, minSdk 23, targetSdk 35. Permissions: INTERNET and CAMERA. Camera hardware is optional; scanner Activity is not exported.

Command: `./build.ps1 -Offline` from the Android directory.

- 55 protocol and aspect-fit assertions passed.
- 26024 bounded render-clock assertions passed.
- 20 HUD / capture pause / brightness policy assertions passed.
- 52 focused pairing, QR and TLS assertions passed: canonical and malformed pairing URI cases, secret-redacted diagnostics, actual generated QR and 90-degree rotated QR decoding, wrong certificate pin / missing certificate rejection, enabled TLS versions, and real local TLS handshakes proving application bytes are sent only with the exact certificate pin.
- Gradle `assembleDebug` and `lintDebug` passed. Lint reports 0 errors and 18 warnings, including the intentional custom certificate-pin trust manager, existing exported read-only display provider, SDK compatibility and untranslated Chinese UI strings. The trust manager accepts only the exact QR-supplied DER hash and is covered by positive and negative TLS handshake tests.
- SDK `apksigner verify --verbose --min-sdk-version 23` passed with v1 and v2 signatures. `aapt dump badging` confirmed the package and version values above.

No device was installed, operated, reconfigured or used for these Android-only checks. Camera hardware behavior, Wi-Fi / USB-network routing, PC interoperability, device rotation and actual display performance require the root task's physical-device integration checks. Existing decoder, rendering clock, HUD logic and requested display-mode logic were preserved.
