# TabLink 0.8.9 Preview 1 verification record

**English (Singapore)** | [简体中文](VERIFICATION-0.8.9.zh-CN.md)

<!-- tablink-version-contract: version=0.8.9; channel=preview; preview=1; androidVersionCode=21 -->

Verification date: 2026-10-02. The release identity is Windows `0.8.9`, Android `0.8.9` / `versionCode 21`, tag `v0.8.9-preview.1`. This record separates offline source gates, device results and public-release results. A 0.8.8 build hash, W202DS frame-rate result, route result or Release download cannot substitute for 0.8.9 verification.

Current finding: **0.8.9 Preview 1 has completed the core W202DS device acceptance, consecutive protected USB session hand-off acceptance, real Windows UI acceptance for the redacted support bundle, and the public prerelease loop.** The release tag points exactly to commit `2a1c3aced048315e3171489fc410c6adeb2eed66`. The full PublicRelease from that commit, CI for the exact commit, four GitHub prerelease assets, public re-download and every-file comparison of the Windows ZIP passed. Device-pass scope covers an in-place upgrade that retained data, a byte-for-byte read-back of the final APK, one virtual display, the device's actual display parameters, the 90 fps encode/decode path, moving the display without disconnecting, physical presentation measurement, two consecutive Windows connections without force-stopping the APK, and exact VDD and reverse-mapping reclamation after each exit. The released `TabLink.exe` separately passed support-bundle preview, cancellation, save, redaction review, locked-existing-target protection and test-file clean-up. That UI snapshot was disconnected and does not extend to connected display and video fields. One-time token replay, QR rotation, revocation, full restart and real route migration remain explicitly untested. The signed `stable` manifest was re-downloaded and verified and remains on 0.8.0.

## Scope of this release verification

The gates target the following failures: migration away from a stable route without sufficient evidence; concurrent use of the single display by two start paths; extension or resurrection of a registration token during migration; loss of clean-up ownership after partial release; disclosure of raw diagnostics or identity in a support bundle; unreviewed or identifiable data in the compatibility catalogue; and version drift across Windows, Android and build scripts.

## Offline source and build gates

These gates do not install the driver, create a virtual display, connect a phone or tablet, or prove real Wi-Fi, USB tethering, MediaCodec or physical-screen behaviour.

| Check | Current result | Boundary |
| --- | --- | --- |
| `eng/version.json`, Windows, Android and the current documents | Passed; 23 isolated runs comprised 2 passing baselines and 21 fail-closed negative cases | Windows/Android 0.8.9, Android build 21 and Preview 1; unknown fields, version/build/Preview/visible-text drift, missing documents and missing or duplicated markers were rejected |
| Windows Release with the browser feature enabled and disabled | Both configurations: 0 warnings / 0 errors | Compilation and static dependency closure only |
| DriverSetup Release | 0 warnings / 0 errors | The driver was neither installed nor removed |
| All managed test projects | 18 / 18 projects passed | E-drive test directories, loopback or fake boundaries |
| Android presentation measurement contract | 36 assertions passed | Fixed vectors check serial-hash binding, health-field mapping, final-sample freshness, four metric names, exclusion of the raw serial, SurfaceFlinger enumeration exit codes, fully qualified ADB path and the pinned Google r37 three-file hashes; ADB and SurfaceFlinger were not called |
| ADB APK installation and protected session hand-off | Core: 54 scenarios; USB recovery: 263 assertions | Fake runners prove arguments, target rechecks and result parsing. Core covers current-user installation, pinned session user, `--no-streaming`, user recheck after Provider publication, one-time markers, serialised transactions, fail-closed Provider errors and standalone install `Success`. USB recovery covers the complete publication → user recheck → marker activation sequence, cancellation or user switch after publication, redaction and retained exact clean-up ownership. W202DS device results are recorded below |
| Trusted-route recovery state machine | 23 scenarios / 69 assertions passed | No real network interface or display API was called |
| Redacted support bundle and diagnostics | 18 scenarios / 164 assertions passed; real released Windows UI preview, cancellation, save, locked-target protection and clean-up passed | The UI snapshot was disconnected and does not prove connected display/video fields in a UI export |
| Compatibility catalogue | 13 scenarios / 128 assertions passed | Static catalogue validation, not proof that a device is compatible |
| Android JVM, `assembleDebug`, `lintDebug`, APK signature | Passed; debug APK 430,494 bytes, SHA-256 `9F672878F77B642B7CC5FD2C2103D2F59174AD8C873215EE1CFA3C2E75CD19E8` | Package `com.tablink.client`, 0.8.9 / build 21, minSdk 23, targetSdk 35; no real Android Keystore, MediaCodec or route migration |
| `git diff --check` and privacy scan of 18 candidate files | Passed | Covered 15 modified tracked files and 3 new files. No personal user directory, project absolute path, real device serial, MAC address or pairing secret was found; matches were loopback/documentation addresses, pinned tool hashes and clearly labelled test vectors |

[GitHub Actions run 36926091128](https://github.com/linjierd/TabLink/actions/runs/36926091128) passed both `windows-managed-tests` and `android-debug-tests` for protected hand-off and USB recovery candidate `6dcc75e65c848643d200787ec9271051e3701a14`. [Run 36927438600](https://github.com/linjierd/TabLink/actions/runs/36927438600) passed the same two jobs for release commit `2a1c3aced048315e3171489fc410c6adeb2eed66`. Annotated tag `v0.8.9-preview.1` points exactly to that commit.

An early local Windows build proved that version fields had changed to FileVersion `0.8.9.0`, but its ProductVersion still named earlier source revision `7049c73dd934dd4d4927797d9b04b9c09aa560d8`, so it is not final-release evidence. A later non-public framework-dependent `-SkipAndroid` packaging smoke test reused the freshly built 0.8.9 debug APK and passed pinned-ADB read-only verification, compatibility-catalogue checks, all managed gates, Windows/DriverSetup publish and the transport self-test. It is development-package evidence only. The final public build was generated again from clean commit `2a1c3aced048315e3171489fc410c6adeb2eed66`.

## Identity and packaging

- [x] A complete `-PublicRelease` was built from clean final commit `2a1c3aced048315e3171489fc410c6adeb2eed66`. `TabLink.exe` has FileVersion `0.8.9.0`, ProductVersion `0.8.9+2a1c3aced048315e3171489fc410c6adeb2eed66` and SHA-256 `CAA9678795270275B0EB5CA2439C6F2C00C342DA93A7FA8E58AFE8184D167C3B`.
- [x] The final Android APK is 346,098 bytes with SHA-256 `34DB1B9F2FD808D8BA7958F1744AA8915677F93FA7DABD820A62C86823F07C7D`, and reports package `com.tablink.client`, version 0.8.9 / build 21 with the pinned Preview signer.
- [x] The public Windows ZIP contains 463 files. Its manifest covers the other 462 paths, sizes and hashes. No PDB, Google ADB three-file set or SIPSorcery binary is present.
- [x] The release version, tag, Android build, filenames, checksums and linked documents agree across source metadata, packages, checksums and GitHub.

## W202DS installation and single-display lifecycle

- [x] The authorised W202DS was upgraded in place from 0.8.8 / build 20 to 0.8.9 / build 21, retaining data. The final candidate was installed in place again with the same pinned signer. The last `--no-streaming -r` transaction returned a standalone `Success`; first-install time stayed unchanged and update time advanced.
- [x] The APK pulled back from the device is exactly 346,098 bytes with SHA-256 `34DB1B9F2FD808D8BA7958F1744AA8915677F93FA7DABD820A62C86823F07C7D`, byte-identical to the final commit build and public download. The package state contains the protected Provider.
- [x] The session created exactly one TabLink VDD and one Android video Surface. Reported logical size was 1920 × 1200, native size 1200 × 1920, rotation 1/4 turn, supported refresh 60 / 90 Hz, and current/requested refresh 90 Hz.
- [x] Moving the display 100 pixels along the adjacent edge and restoring it did not disconnect. Sent and presented counts advanced, while VDD and Surface counts remained one.
- [x] About 7.205 seconds after TabLink exited, the TabLink VDD count returned from 1 to 0 and active desktops from 2 to 1. The ToDesk virtual display adapter was unaffected.
- [x] Two consecutive cycles of “start Windows candidate → receive real presentation → exit normally” passed without force-stopping, clearing or reinstalling the Android app between them. The second cycle took over while the Android Session was retrying the first cycle's old endpoint. Each exit left zero Windows TabLink processes, zero TabLink VDDs and zero reverse mappings for the selected device.
- [ ] This run did not separately inject preparation failure or first-presentation timeout, and did not separately press the UI **Stop connection** action. Offline state-machine coverage does not turn these into new device-pass claims.

The verification touched only the selected authorised tablet. It did not stop a shared ADB server, switch USB mode or operate another attached device.

## Pairing, revocation and full restart

- [ ] Replay of a registration token after one successful use is rejected on a device before display preparation.
- [ ] Creating two QR codes in sequence immediately retires the first; the second still lasts at most five minutes and registers only once.
- [ ] Two connections by one trusted device receive different challenges, and an old challenge signature cannot authenticate the new connection.
- [ ] Revoking an active device stops its connection, rejects retained automatic reconnect, and requires registration again.
- [ ] After a full computer restart, the computer identity, pinned certificate and registered device complete a fresh challenge reconnect without persisting a bearer token.

## Real route recovery

- [ ] After an IPv4 change on the same Wi-Fi interface, recovery waits for the single stable candidate to meet the three-enumeration / eight-second threshold; the debounce does not stop the session or remove the VDD.
- [ ] After recreation of the same USB-network device or a change of interface index, exact device identity remains constrained and an ambiguous candidate does not migrate.
- [ ] Changing explicitly from Wi-Fi to USB tethering and back rebuilds against the selected route instead of guessing from the UI's current item.
- [ ] Recovery failure retries only the confirmed pinned route and does not silently migrate to another interface.
- [ ] A live QR code carries only its original remaining lifetime through migration. The old listener token is rejected, retry does not extend it, and a consumed or expired token does not revive.
- [ ] A single interface-enumeration fluctuation cannot migrate while recent real presentation continues.

## Redacted support-bundle UI

- [x] A real released Windows UI completed **Preview → Save**. The complete preview copied by the user rebuilt to 4,289 UTF-8 bytes. All four body-entry sizes and SHA-256 values matched its manifest. The five-entry saved snapshot also rebuilt to 4,289 bytes under the production formula and passed both the ordinal pre-write preview-consistency gate and the post-write byte, size and hash checks. Opening Preview again creates a fresh snapshot, so hashes are compared only within one fixed preview/save round.
- [x] The ZIP contains exactly `manifest.json`, `compatibility.json`, `diagnostics.json`, `issue-summary.txt` and `README.txt`, with no duplicate, directory or extra entry. Strict UTF-8, fixed timestamps, zero external attributes, typed field allow-lists and fixed privacy text passed. After structured exclusions for version and timestamp fields, scans of the catalogue, diagnostics and summary found zero user names, absolute/UNC paths, URLs, canonical IPv4 values, MAC addresses or key material. The manifest explicitly excludes raw logs, USB/ADB/PnP/host/device identities, pairing material, settings, trust stores, display leases, USB receipts and screenshots.
- [x] Cancelling the save dialog left zero target ZIP or staging files. With an existing 2,839-byte target locked, confirming overwrite through the real UI displayed the fixed write-permission summary, preserved SHA-256 `1979E6A1D04A83B838D1052F6EF5278F0D53AFFD1649C01D37418FDD833E8E2A` byte-for-byte and left zero `.tmp` files. After unlocking, the target opened exclusively again. The test ZIP was deleted, with zero ZIP and `.tmp` files remaining and no upload action during the check.

## Frame rate and physical presentation

0.8.9 does not change decoder, encoder or display-driver performance. Requested refresh, decode submission, presentation callbacks and physical presentation are separate measurements. A callback value is not a SurfaceFlinger or camera measurement.

| Metric | 0.8.9 result | Method |
| --- | ---: | --- |
| Tablet supported / current panel Hz | 60 / 90; current 90 | APK display capability and active-mode read-back |
| Windows requested Hz | 90 | Session target; NVENC effective encoding 90 fps |
| Windows presentation-callback delta | 90.1825 fps | `session-health.measuredPresentedFps`; callback delta only |
| Android decode submission | 89.9522 fps | `session-health.ClientSubmittedFps` |
| Android presentation callback | 90.0041 fps | `session-health.ClientPresentedFps` / `OnFrameRenderedListener`; not physical presentation |
| Physical presentation | 86.9661 fps | SurfaceFlinger actual-present timestamps from `Measure-AndroidPresentation.ps1` |

The continuous sample lasted 30.115 seconds and observed 2,619 new actual-present events. P95 / P99 / maximum presentation gaps were 11.141 / 22.220 / 33.346 ms. An estimated 93 vertical-sync slots were missed, with zero ring-buffer coverage gaps and no stalled tail. Missed sync slots are timeline estimates from actual-present intervals at 90 Hz; they do not mean 93 network packets, decoder frames or distinct visible images were lost. Raw measurement and latency files remain in the private, uncommitted E-drive verification directory. Public documents contain neither the raw device serial nor its binding value.

## Preview release gate and known boundaries

The hardware gate for 0.8.9 Preview 1 is: in-place upgrade retaining data; unique target and one virtual display; actual device orientation, resolution and refresh; continued video presentation; position changes without disconnection; exact VDD reclamation after exit; full public build from a clean commit; CI for that exact commit; annotated tag; four Release assets; and byte-for-byte verification after public HTTPS download. Those core items passed.

One-time token replay, QR rotation, per-connection challenge values, active revocation, trusted reconnect after a complete computer restart, real Wi-Fi / USB-tethering route migration and a separate injected failure through the UI **Stop connection** action remain unchecked. Offline protocol and state-machine tests do not equal device acceptance. These gaps do not block a release clearly labelled Preview, but must not be described as device-verified. The redacted support bundle passed the disconnected-snapshot UI acceptance described above; that does not extend to connected display or video fields.

## Public release

- [x] The full `-PublicRelease` ran from the clean final commit without `-SkipAndroid`.
- [x] CI passed on the exact release commit, and annotated tag `v0.8.9-preview.1` points to it.
- [x] The GitHub Release is a prerelease containing the Windows ZIP, Android APK, corresponding FFmpeg source and `SHA256SUMS.txt`.
- [x] Each public asset's name, byte size and SHA-256 was recorded, downloaded again over public HTTPS and compared. Exact values are in the [0.8.9 release notes](RELEASE-0.8.9.md#public-release-assets).
- [x] Every file in the public Windows ZIP was checked against the manifest. The 463 files match the final PublicRelease and contain no PDB, personal path, private diagnostic material or unlicensed binary.
- [x] The signed `stable` manifest was downloaded again and verified with the repository-pinned P-256 public key. It remains Windows 0.8.0 / build 800 and Android 0.8.0 / build 11; publishing this Preview did not advance the stable update channel.
- [x] README download links point to 0.8.9 Preview 1.

0.8.9 Preview 1 therefore has a full final-commit PublicRelease, successful exact-commit CI, annotated tag, GitHub prerelease, public re-download comparison, unchanged verified `stable` 0.8.0 channel and real released-UI acceptance of the disconnected redacted support bundle. Token/QR/per-connection challenge, revocation, full restart and real route migration remain explicit Preview limitations.
