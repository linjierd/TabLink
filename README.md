# TabLink · One device, one extended display

**English (Singapore)** · [简体中文](README.zh-CN.md)

<!-- tablink-version-contract: version=0.8.9; channel=preview; preview=1; androidVersionCode=21 -->

[![CI](https://github.com/linjierd/TabLink/actions/workflows/ci.yml/badge.svg)](https://github.com/linjierd/TabLink/actions/workflows/ci.yml)
[![Licence: MIT](https://img.shields.io/badge/TabLink%20code-MIT-blue.svg)](LICENSE)

TabLink turns one Android phone or tablet into an independent Windows extended display. The Windows host creates one virtual display, captures and encodes it as H.264, and sends it to the Android client, which decodes the stream and can return single-finger pointer input.

The current public prerelease is **0.8.9 Preview 1**. Its Windows version is `0.8.9`; the Android identity is `0.8.9` / build `21`; and the transport protocol remains v1. The annotated tag [`v0.8.9-preview.1`](https://github.com/linjierd/TabLink/releases/tag/v0.8.9-preview.1) points exactly to commit [`2a1c3aced048315e3171489fc410c6adeb2eed66`](https://github.com/linjierd/TabLink/commit/2a1c3aced048315e3171489fc410c6adeb2eed66). The same commit passed both jobs in [GitHub Actions run 36927438600](https://github.com/linjierd/TabLink/actions/runs/36927438600).

The signed public `stable` update channel was downloaded and verified again after the prerelease was published. It remains at **0.8.0** — Windows build `800`, Android build `11`, rollout `100%`. Installing or publishing this Preview does not advance `latest` or the stable channel. See the [0.8.9 release notes](RELEASE-0.8.9.md), [0.8.9 verification record](VERIFICATION-0.8.9.md), and [stable update design](AUTO-UPDATE.md).

> **Project vision**
>
> I want to keep making free, useful small software that solves real problems. If you have an idea, a feature request, or a problem that software could help with, please [open an issue](https://github.com/linjierd/TabLink/issues).

## Download 0.8.9 Preview 1

This is a prerelease. Extract the Windows ZIP completely before running `TabLink.exe`, and install the APK on the receiving Android device. Verify every download against `SHA256SUMS.txt`.

| Asset | Size | SHA-256 |
| --- | ---: | --- |
| [Windows x64 complete package](https://github.com/linjierd/TabLink/releases/download/v0.8.9-preview.1/TabLink-Windows-x64-0.8.9-preview.1.zip) | 97,320,509 bytes | `4448CCA4151247F535139DBEC5A9D62331E9EB66E89491E0AEB47481D2726206` |
| [Android APK](https://github.com/linjierd/TabLink/releases/download/v0.8.9-preview.1/TabLink-Android-0.8.9-preview.1.apk) | 346,098 bytes | `34DB1B9F2FD808D8BA7958F1744AA8915677F93FA7DABD820A62C86823F07C7D` |
| [FFmpeg 7.0.2 corresponding source](https://github.com/linjierd/TabLink/releases/download/v0.8.9-preview.1/TabLink-FFmpeg-7.0.2-corresponding-source.tar.gz) | 28,919,316 bytes | `FD7977F53EDD262D55C49F200EB5F54B1B12F5FFA547770380448708D75EA6F2` |
| [SHA256SUMS.txt](https://github.com/linjierd/TabLink/releases/download/v0.8.9-preview.1/SHA256SUMS.txt) | 326 bytes | `D120A47FA25634F6F8AC33071D5039FB4339A3AE9E6D4EAEC6D9EBAD00B07EA5` |

All four assets were downloaded again from their public GitHub Release HTTPS addresses and checked. The extracted Windows ZIP contains 463 files and matches the final PublicRelease byte for byte.

The native Android client supports Android 6.0 / API 23 and later. Actual H.264 capability, display modes, and refresh behaviour still depend on the device.

## What TabLink provides

- One independent Windows extended desktop on one receiving device at a time.
- Native Android orientation, resolution, active refresh rate, and supported display-mode discovery.
- H.264 encoding with NVENC, QSV, and AMF candidates; an explicitly authorised x264 software fallback is available as a separate GPL helper.
- Android MediaCodec selection, bounded decoder recovery, frame-submission evidence, presentation-callback evidence, and receiver feedback.
- Wi-Fi and ordinary USB tethering without developer mode.
- An ADB-over-USB compatibility route for devices and networks where the native route is unsuitable.
- A tray-resident Windows host: closing the window with **×** hides it while the active session continues.
- A Windows **Settings** page where the footer attribution can be shown or hidden and its author, GitHub, and blog text and HTTPS links can be customised without restarting or disconnecting the display.
- Display-position changes without intentionally tearing down the session; capture and touch mapping are refreshed against the same virtual display.
- A configurable full-screen Android receiver with a small status overlay and optional single-finger pointer control.
- Device exclusion rules, trusted-device revocation, connection health stages, bounded repair actions, adaptive quality, and an explicitly exported privacy-filtered support bundle.

TabLink never creates a second, third, or fourth TabLink display. Registered devices do not increase the display limit. Connecting reserves one receiver and one TabLink virtual display; stopping the session releases that display.

## Interface languages

TabLink provides **English** and **Simplified Chinese** interfaces across the Windows host, Android client, optional browser receiver, and the source projects for iOS / iPadOS and HarmonyOS NEXT. Each client offers **Follow system**, **简体中文**, and **English**. A new installation follows the operating-system language: `zh-*` uses Simplified Chinese, and every other language uses English.

The selected mode is stored locally on that client and is independent of pairing, trusted-device records, device exclusions, display leases, update policy, and the custom author footer. Changing the language refreshes user-facing controls, connection status, update text, diagnostics guidance, and receiver messages without recreating the pairing or intentionally disconnecting an active display. Protocol names, URLs, hashes, device identifiers, and machine-readable diagnostic values remain unchanged.

The public Windows and Android packages contain both languages. The browser receiver keeps its choice in browser-local storage. The Apple and HarmonyOS implementations also contain both resource sets and persistent language selection in source, but those two native projects still require their platform toolchains, signing, store publication, and real-device acceptance before they can be offered as downloads.

## Connect over Wi-Fi or USB tethering

The native network route does **not** require developer mode, USB debugging, wireless debugging, or network ADB.

1. Install the [0.8.9 Preview 1 APK](https://github.com/linjierd/TabLink/releases/download/v0.8.9-preview.1/TabLink-Android-0.8.9-preview.1.apk) through Android's normal installer.
2. For Wi-Fi, put the computer and Android device on the same local network. For USB, connect the cable and enable **USB tethering** in Android system settings.
3. Start `TabLink.exe` and accept the normal Windows administrator prompt. Open **Wi-Fi / USB without debugging**, refresh the routes, and select the actual WLAN, Ethernet, or Android USB-network interface that should carry TabLink traffic.
4. For first-time registration, generate a pairing QR code and scan it from the Android app. If the camera is unavailable, copy and paste the connection link.
5. On later connections, choose the registered computer in the Android app. The client verifies the pinned computer certificate and signs a fresh challenge with its Android Keystore P-256 key.

The QR token is a random 256-bit, one-time registration secret with a five-minute lifetime. It is consumed before the computer stores the device key and name. It is not retained for trusted reconnects. UDP port `27193` is used only for same-subnet discovery; the assigned TCP listener carries the TLS-protected session. Discovery responses contain no pairing token, trusted-device list, or private connection link.

If the computer's address changes, TabLink can rediscover the same registered host. A listener migration is accepted only after one candidate for the same physical route is observed at least three times over at least eight seconds. Ambiguous candidates, enumeration failures, clock rollback, or recent real presentation activity prevent a speculative migration. TabLink does not change the default route or DNS and does not issue Android Open Accessory mode-switch commands.

Android 13 does not let an ordinary APK silently enable USB tethering reliably, so the user must still switch it on in system settings. USB tethering may itself expose an Internet gateway or DNS server to Windows; TabLink binds its own traffic to the selected interface but does not rewrite Windows routing.

## Connect over USB debugging

The compatibility route requires Android developer options and USB debugging:

1. Exit any other USB-display service that may change the Android device's USB mode.
2. Connect the device, enable USB debugging, and approve this computer on the Android prompt.
3. In TabLink, select a complete Google **SDK Platform-Tools r37.0.0 for Windows** installation. TabLink accepts only the pinned hashes for `adb.exe`, `AdbWinApi.dll`, and `AdbWinUsbApi.dll`, then copies the verified trio to `%ProgramData%\TabLink\Adb\sha256-<manifest-hash>\`.
4. Refresh devices, select the intended receiver, install the bundled APK if required, and choose **Connect selected device**.

Every ADB command remains bound to the selected serial number and the fixed Android foreground user for that session. Installation uses a non-streaming, data-preserving update and reports success only after ADB returns a distinct `Success` result. The public archive does not redistribute Google's ADB binaries.

The default exclusion list protects the ZTE F50 Pro USB-tethering identities `19D2:0246` and `19D2:0621`. A serial-number or VID/PID match blocks selection and APK installation, which prevents TabLink from taking over an excluded modem.

Exclusion rules can be added and removed in the Windows app. A protected device should remain excluded by serial number when available, or by its exact VID/PID combination when a stable serial number is unavailable.

## Other receiver paths

The source tree contains optional browser-receiver support for devices that cannot run the Android client. It uses locally hosted HTTPS/WebRTC and requires the receiving device to trust the local certificate once. The public Windows archive deliberately builds with that feature disabled and excludes the SIPSorcery browser binary because its additional upstream restriction prevents unrestricted worldwide redistribution. The normal source build includes the dependency, so anyone building or redistributing that variant must first review and comply with its terms.

Native iOS/iPadOS and HarmonyOS NEXT projects are also present under `native/apple` and `native/harmony`. They remain source-level work: no Apple Developer or AppGallery signing, store release, or real-device acceptance has been completed, so 0.8.9 Preview 1 does not offer those clients as downloads.

## Display and session lifecycle

At connection time, TabLink confirms the receiver and its display capabilities before preparing the single TabLink virtual display. If the virtual display device is absent, the Windows host installs or recreates it and applies the required mode. If preparation cannot be verified, the connection stops rather than capturing the main screen or another product's virtual display.

Stopping the connection first ends video and input, releases pointer state, returns the display lease, and removes the active TabLink virtual display device. The signed driver package and TabLink's configuration remain available for the next connection. Exiting from the tray performs the same clean-up; clicking **×** merely hides the window.

The host requires administrator elevation at startup. During a Windows secure-desktop event such as a UAC prompt or lock screen, TabLink preserves the authenticated session, sends a paused state, and keeps the last frame on Android. It resumes ordinary capture after returning to the user desktop. It does not display, capture, or interact with the protected desktop.

The same virtual display may be moved in **Settings → System → Display** while connected. TabLink revalidates its identity and updates capture coordinates and touch mapping. A compatible position change was verified without disconnecting the 0.8.9 ADB session.

## Android full-screen receiver

The Android client enters full-screen mode without a permanent control bar. Its overlay can show:

- panel Hz and requested Hz;
- submitted decode fps and presentation-callback fps;
- the current decoder name and recovery state.

Long-press the overlay, or use the Android Back gesture or key, to open display settings. You can choose any of nine positions, select white, green, cyan, yellow, black, or a custom `#RRGGBB` colour, and set transparency from 0% to 100%. Changes are saved immediately. The default text is white with 30% transparency.

## Measured 0.8.9 hardware result

The public compatibility entry `tlc-000002` records one narrowly scoped test of 0.8.9 Preview 1:

| Item | Recorded result |
| --- | --- |
| Host | Windows 11 x64, NVIDIA RTX 4060 Laptop GPU |
| Receiver | ZTE W202DS, Android 13, native Android client |
| Connection | ADB compatibility route |
| Display | 1920 × 1200 logical; 1200 × 1920 native; one quarter-turn rotation |
| Refresh rates | 60 / 90 Hz supported; 90 Hz active and requested |
| Encoder | NVENC, 90 fps requested and effective |
| Windows presentation callback | 90.1825 fps |
| Android decode submission | 89.9522 fps |
| Android presentation callback | 90.0041 fps |
| Physical presentation | 86.9661 fps from SurfaceFlinger actual-present timestamps |
| Physical sample | 2,619 presents over 30.115 seconds |
| Intervals | P95 11.141 ms; P99 22.220 ms; maximum 33.346 ms |

The 0.8.9 evidence retained no decoder name or decoder tier, so both catalogue fields are deliberately `unknown`. The 0.8.8 hardware-decoder result is not reused for 0.8.9. Requested Hz, encoder output, Android submission, presentation callback, and physical presentation are separate measurements; none should be substituted for another.

The same 0.8.9 receiver completed two consecutive Windows start → real presentation → normal exit cycles without force-stopping, clearing, or reinstalling the Android app between them. Each cycle used one TabLink virtual display, and each exit returned the TabLink process, virtual-display count, and target-device reverse mapping to zero. The physical measurements and their limits are documented in the [verification record](VERIFICATION-0.8.9.md).

## Adaptive video and recovery

TabLink keeps the receiver's reported resolution, orientation, and requested refresh mode fixed for a session. Adaptive quality changes only the H.264 bitrate and GOP within a selected encoder backend. It uses receiver feedback to make bounded, hysteretic changes; stale, paused, or insufficient evidence freezes adaptation.

If the Android decoder queue loses its reference chain because of overflow or a 150 ms expiry, the negotiated `decoder-refresh-v1` path requests the next IDR at a bounded rate. Windows retains the same authenticated connection, encoder, and virtual display. A decoder recovery is complete only after the replacement decoder produces a new presentation callback.

The health centre reports route/listener, authentication and screen parameters, the unique virtual display, capture/encode/send, Android decode submission, and Android presentation callback as separate stages. A **Safe repair** action is enabled only for a stage with an explicit actionable state.

## Signed stable updates

Every native client uses the author's blog as the primary signed `stable` manifest source and the latest non-prerelease GitHub Release as the fallback:

```text
https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json
https://github.com/linjierd/TabLink/releases/latest/download/manifest.json
```

Both addresses are transport mirrors rather than trust anchors. Every manifest envelope must pass ECDSA P-256 / SHA-256 verification with the public key in `updates/stable-public-key.spki.base64` before the client parses a payload that fixes the platform, stable SemVer, build number, HTTPS URL, byte size, SHA-256, and rollout percentage. When both sources are valid, the newer signed `publishedAtUtc` decision wins; conflicting decisions at the same timestamp fail closed, and a newer signed pause cannot be bypassed by an older mirror. The conflict timestamp is persisted across checks and restarts: ordinary decisions at or before it remain blocked until a valid signed decision with a strictly later timestamp is accepted. The private signing key is not stored in the repository or release packages. Invalid signatures, altered packages, downgrade attempts, duplicate platforms, unknown fields, and prerelease versions fail closed. Preview and prerelease GitHub Releases never advance this stable route.

Windows, Android, iOS/iPadOS, and HarmonyOS NEXT persist the highest accepted publication time together with a canonical decision digest that excludes only package download URLs. A later process restart or one-source outage therefore cannot replay an older still-signed manifest after the device has seen a newer pause. At the same timestamp, only URL-different packages with identical platform, version, build, size, hash, rollout, protocol, installer destination, and notes qualify as mirrors.

As at 2 October 2026, the blog endpoint returns HTTP 530 and the GitHub fallback returns 404 because every public Release is still a Preview. End-to-end automatic updating therefore remains unavailable until either source publishes a valid signed stable manifest. Clients fail closed and never substitute a Preview asset or unsigned content. Once either source is available, an outage at the other source does not prevent verification and use of the valid source.

The **Settings** page provides the same three policies on all four native clients:

| Policy | Check and download | Install |
| --- | --- | --- |
| **Automatic** | Check on launch or foreground entry and then periodically; Windows/Android download a verified stable build while idle, while store clients prepare verified store metadata | Windows installs after the display session has stopped; Android rechecks the latest signed decision and starts the system installer without another in-app confirmation, while retaining Android's system confirmation; App Store/AppGallery and system settings control store download and automatic installation |
| **Download, then ask** (`DownloadThenAsk`) | Check automatically; Windows/Android download the verified package, while Apple/Harmony verify the store update without pretending to pre-download a store package | The user must explicitly choose **Install** or open the store; Android still requires its system confirmation |
| **Never** | Make no background update request and download nothing | Do not install; the install action stays disabled while this policy is selected |

The default for a new preference file is **Automatic**; a damaged preference file fails closed to **Never**. These three choices are present in the Windows, Android, unpublished iOS/iPadOS, and unpublished HarmonyOS NEXT clients. Changing the selection takes effect immediately and does not disconnect the display. Windows defers update work while the display is active. Protected replacement, whether automatic or explicitly requested, runs only from the exact `%ProgramFiles%\TabLink` installation. Portable copies on `E:`, Desktop, OneDrive, or another folder may check and download according to policy, but they cannot replace themselves. Relevant protected state is kept under:

| Path | Purpose |
| --- | --- |
| `%LOCALAPPDATA%\TabLink\updates\` | Verified manifest, download cache, and pending state |
| `%ProgramData%\TabLink\Updater\sha256-<package-hash>\` | Content-addressed protected updater |
| `%ProgramData%\TabLink\Transactions\<transaction-id>\` | Same-volume staging, backup, failed version, and update log |

The updater verifies the signed manifest, package size and hash, protected ACLs, extracted staging tree, and old-tree snapshot before swapping directories. A new build must produce a challenge-bound health signal; otherwise, the updater restores the verified old tree. Android rechecks the manifest, APK size, hash, package name, version, build, and installed signing identity before handing the APK to `PackageInstaller`; Android always retains the system installation-confirmation step.

Browser assets update with the Windows host. The unpublished Apple and HarmonyOS adapters accept only a store URL carried by the same signed manifest, while App Store or AppGallery remains responsible for installation. No iOS/iPadOS or HarmonyOS package is currently present in the stable manifest.

## Privacy, feedback, and support bundles

Use the repository's [Issue Forms](https://github.com/linjierd/TabLink/issues/new/choose) for bugs, performance findings, device compatibility, and feature requests. Report vulnerabilities through a [private GitHub Security Advisory](https://github.com/linjierd/TabLink/security/advisories/new), not a public issue.

The Windows **Diagnostics and logs** page can export a privacy-filtered support ZIP. Before saving, it displays the complete textual contents. Version 1 creates only:

- `manifest.json`
- `compatibility.json`
- `diagnostics.json`
- `issue-summary.txt`
- `README.txt`

The bundle is generated from a typed allow-list and does not read or copy raw logs, screenshots, settings, trust stores, USB receipts, display leases, serial numbers, network addresses, local paths, pairing links, tokens, certificates, or private keys. TabLink neither uploads nor attaches the bundle automatically. It leaves the computer only if the user saves, reviews, and chooses to share it.

Footer attribution preferences are stored only in `%LOCALAPPDATA%\TabLink\author-footer.json`. They are separate from USB exclusion and approval policy, are never included in the support bundle, and do not change the official repository authorship or licence. Only absolute HTTPS links without embedded credentials are accepted.

## Compatibility catalogue

The [public compatibility catalogue](compatibility/README.md) is a maintained record of specific historical observations. Each entry is tied to one TabLink version, source commit, host/GPU, receiver, connection method, display parameters, and evidence document. A missing entry does not mean unsupported; one verified combination does not imply that every OS version, computer, route, or unit of the same model will work.

Compatibility issues and support bundles are only candidate evidence. A maintainer must review and enter non-identifying facts into the controlled JSON. Its schema and validator reject unknown fields, inconsistent feature claims, missing commit/document closure, and common patterns for serial numbers, USB/PnP identifiers, addresses, paths, tokens, certificates, and keys. The catalogue is static documentation: the Windows and receiver apps do not download it, and it does not change runtime device or codec selection.

## Known Preview boundaries

0.8.9 Preview 1 has these explicit limits:

- Only one receiver and one TabLink virtual display may be active.
- Audio, pressure-sensitive pen input, and multi-touch are not implemented.
- Token replay on real hardware, QR rotation, proof that consecutive connections receive different challenges, active revocation during a real session, a full computer restart, real Wi-Fi/USB route migration, the connected-state support-bundle UI, and the standalone **Stop connection** fault path remain unverified.
- The final 0.8.9 catalogue records the receiver decoder and video decoder tier as `unknown`; it does not claim hardware decoding.
- QSV and AMF are built as hardware candidates, but successful encoding remains unverified on suitable Intel and AMD computers. Failure isolation was observed on the current non-matching host.
- The public Windows archive excludes Google ADB binaries and the SIPSorcery browser-receiver binary.
- iOS/iPadOS and HarmonyOS NEXT source projects are present, but no signed native app, store listing, or real-device acceptance is published.
- The Windows executable is not Authenticode-signed and can appear as an unknown publisher. The included upstream virtual-display driver has its existing signature; this project does not claim WHQL certification.
- Actual frame rate depends on capture, encoding, network/USB transport, Android decoding, and panel policy. A requested 90 Hz mode is not a promise of 90 physically presented frames per second.

Offline state-machine and protocol tests cover several of these paths, but they are not substitutes for the missing real-device or full-system checks.

## Repository layout and building

The version identity is centralised in [`eng/version.json`](eng/version.json). Windows, Android, release documents, build outputs, and CI are checked against it before expensive work begins.

| Path | Contents |
| --- | --- |
| `src/TabLink.Core` | Device policy, configuration, screen parsing, and serial-bound ADB operations |
| `src/TabLink.Windows` | Windows UI, display ownership and lifecycle, capture, encoding, transport, touch, tray, and watchdog |
| `src/TabLink.Updater` | Transactional Windows update, launch health check, and rollback |
| `src/TabLink.DriverSetup` | Explicit driver installation, mode configuration, and targeted device maintenance |
| `android` | Native Android client, MediaCodec receiver, display capability provider, and Gradle build |
| `native/apple`, `native/harmony` | Unpublished iOS/iPadOS and HarmonyOS NEXT native projects |
| `compatibility` | Controlled catalogue, schema, generated Markdown, and validator inputs |
| `updates`, `tools/TabLink.ReleaseTool` | Signed-manifest format, public key, release staging, and verification tooling |
| `tests/TabLink.Core.Tests` | Core regression harness that does not require a real device |

From the repository root:

```powershell
dotnet run --project tests/TabLink.Core.Tests -c Release
.\build.ps1 -SkipAndroid
```

To create a complete self-contained package for use on your own Windows computer, including the browser receiver and bundled ADB, use a new empty directory:

```powershell
.\build.ps1 -LocalFullBuild -OutputDirectory E:\TabLink-local-full
```

The browser receiver currently depends on SIPSorcery 10.0.16. Review its bundled licence before sharing that local package; the globally downloadable public archive continues to exclude this dependency.

Building from source requires the .NET 10 SDK and the Android toolchain for Android work. The public Windows x64 ZIP is self-contained and does not require a separate .NET runtime. A public build may not use `-SkipAndroid`; it checks the fixed Android package identity and Preview signing certificate, produces a self-contained Windows build and a non-debuggable APK, copies the applicable licences and complete FFmpeg/x264 corresponding source, and generates `SHA256SUMS.txt`.

CI runs Windows managed tests in both browser-feature configurations, DriverSetup builds, Android JVM tests, `assembleDebug`, `lintDebug`, APK signing checks, and the cross-platform version contract. Automated tests do not install a driver, create a real display, call ADB, or prove physical MediaCodec, network migration, or display behaviour.

## Licence and third-party software

Code written for TabLink is released under the [MIT Licence](LICENSE). The repository tracks source, tests, patches, provenance, and notices; local diagnostics, device identifiers, build caches, downloaded ADB/FFmpeg binaries, APKs, and complete release packages are excluded from Git history.

Third-party components retain their own terms; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Key components include:

- VirtualDrivers Virtual Display Driver 25.7.23 — MIT.
- FFmpeg 7.0.2 hardware helper — LGPL-2.1-or-later, with NVENC, QSV, and AMF.
- Separate FFmpeg 7.0.2 + x264 helper — GPL-2.0-or-later and used only with explicit authorisation.
- SIPSorcery 10.0.16 — BSD-3-Clause plus an additional geographic/use restriction. That restriction is not OSI-approved, so a combined binary containing it must not be described as unrestricted MIT software.
- Google Android SDK Platform-Tools 37.0.0 — obtained separately; public TabLink packages do not redistribute ADB.

## Author and contributing

- **张林杰 (Jey)** · GitHub: [@linjierd](https://github.com/linjierd)
- Blog: [Linjie / Development Notes](https://linjie.space/)

Author and project links are also recorded in [AUTHORS.md](AUTHORS.md). Contributions are welcome; please read [CONTRIBUTING.md](CONTRIBUTING.md), keep claims within their measured evidence, and use the issue forms for proposals or reproducible reports.
