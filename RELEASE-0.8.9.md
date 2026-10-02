# TabLink 0.8.9 Preview 1: reliable route recovery and reviewable support

**English (Singapore)** | [简体中文](RELEASE-0.8.9.zh-CN.md)

<!-- tablink-version-contract: version=0.8.9; channel=preview; preview=1; androidVersionCode=21 -->

TabLink 0.8.9 Preview 1 has completed its core W202DS device acceptance, consecutive protected USB session hand-off acceptance and public prerelease loop. It brings route recovery, safe clean-up, redacted diagnostics and public compatibility evidence introduced after 0.8.8 under a new release identity: Windows `0.8.9`, Android `0.8.9` / `versionCode 21`. The following passed: an in-place Android upgrade that retained app data, the single-display lifecycle, the device's actual 1920 × 1200 / 90 Hz mode, moving the display without disconnecting, physical presentation measurement, two consecutive Windows session hand-offs, VDD reclamation after exit, a final-commit build, CI for that exact commit, an annotated tag, a GitHub prerelease and public download verification. The signed public `stable` update channel was verified again after the prerelease and remains on 0.8.0.

## Protected USB session hand-off

0.8.9 no longer passes a USB session token and port directly to an Android Activity that another app could start. Windows first publishes a configuration through a Provider protected by `android.permission.DUMP`. That configuration exists only in the APK process memory, expires after 30 seconds and can be consumed only once. Windows then wakes the Activity with a random 128-bit activation marker. The Activity does not accept raw token or port extras. An ordinary launcher start, forged extras or an incorrect marker cannot replace the session, and ADB cannot silently take over a trusted network session.

A new Windows session can explicitly stop a USB Session that is still retrying an old random port, then apply the new protected configuration. The Android foreground user is checked again between publication and activation, and the complete transaction is serialised. A Provider error, user switch, cancellation or concurrent launch fails closed. On these failures, the recovery path retains ownership of the exact reverse mappings it created and removes only this session's endpoints.

## Recovery after a route change

0.8.9 gives an authenticated native-network connection a stable route identity. After the old binding disappears, migration is allowed only when one candidate matching that identity succeeds in three consecutive enumerations spanning at least eight seconds. Enumeration failure, multiple candidates, a changed candidate, clock rollback, an unrelated interface or recent real presentation resets the evidence. The identity covers the connection kind, interface identifier, alias, index, IPv4 address, prefix and, where applicable, USB device identity. Firewall bindings use the same identity.

Native, ADB-compatible, browser and additional native entry points share one process-wide atomic start lease so two starts cannot occupy the single display. A migration request can be claimed only once. Recovery retries only the route that was already confirmed; it never guesses a different route from the current UI selection. If resources are not fully released, the native-network session retains pending clean-up ownership so an exact retry remains possible instead of recording an incomplete clean-up as successful.

## Migration does not extend a pairing QR code

Before route migration, an unconsumed registration token on the old listener is atomically retired. The new listener can inherit no more than the token's remaining lifetime, bounded by both UTC and monotonic deadlines. A retry does not extend the five-minute lifetime, and a consumed or expired token cannot return. When no trusted device and no valid registration opportunity remain, the user must explicitly create a new pairing QR code.

## Redacted support bundle and feedback routes

The Windows **Diagnostics and logs** page lets the user explicitly export a support bundle. Before saving, it shows a text preview that corresponds byte-for-byte to the ZIP content. The ZIP contains exactly five entries: `manifest.json`, `compatibility.json`, `diagnostics.json`, `issue-summary.txt` and `README.txt`. It is generated from a typed allow-list of structured fields. It does not read or copy raw logs, settings, trust stores, USB receipts, display leases, screenshots, serial numbers, network addresses, paths, tokens, certificates or keys, and it is never uploaded automatically.

The released `TabLink.exe` has now passed real Windows UI checks for preview, cancellation, save and protection of a locked existing target. Cancellation writes nothing. The saved five-entry ZIP passed entry ordering, strict UTF-8, manifest size and SHA-256, field allow-list and privacy checks. When an existing target was locked, the failed save preserved that file and left no staging file. The test ZIP was deleted after inspection. This UI snapshot was taken while disconnected, so it is not evidence for connected display and video fields in a UI export.

The repository provides Bug, Performance, Device compatibility and Feature request forms. Report security vulnerabilities through a private GitHub Security Advisory so device and authentication data are not exposed in a public Issue.

## Curated compatibility catalogue

`compatibility/catalog.json` is a manually reviewed static evidence catalogue. A strict validator generates its Schema and Markdown view. The catalogue rejects unknown fields, inconsistent capability combinations, evidence without a matching document and commit, common address/path/token/device-identifier patterns, reparse-point boundary escapes and non-atomic writes. It does not use telemetry, import Issues or support bundles automatically, or download data to the client.

After the release, the public catalogue gained `tlc-000002`, bound exactly to 0.8.9 Preview 1, release commit `2a1c3aced048315e3171489fc410c6adeb2eed66` and this W202DS ADB device evidence. The record claims only the independently verified desktop, native orientation, 90 Hz mode and single-display clean-up from this run, with SurfaceFlinger physical presentation at 86.966 fps. The retained evidence did not record the decoder name, so the catalogue says `unknown` instead of borrowing the 0.8.8 result. It retains the limitations that USB debugging was required and a full host restart was not tested. It does not rewrite the 0.8.8 historical record or mark untested network registration, revocation or route migration as passed.

## Build and version gates

`eng/version.json` is the source for the current Preview identity. The Windows project version, Android `versionName`, Android `versionCode`, current release documents and build output paths must match it. The root build, Android build and GitHub Actions check this contract before expensive work. Android artifact names and `aapt` identity checks continue to come from the actual Gradle version.

CI covers both Windows browser-feature configurations, DriverSetup, every managed test project, Android JVM tests, `assembleDebug`, `lintDebug` and APK signature verification. See the [0.8.9 verification record](VERIFICATION-0.8.9.md) for complete current results and hardware boundaries.

[GitHub Actions run 36926091128](https://github.com/linjierd/TabLink/actions/runs/36926091128) for protected hand-off and USB recovery commit `6dcc75e65c848643d200787ec9271051e3701a14` passed both the Windows and Android jobs. [GitHub Actions run 36927438600](https://github.com/linjierd/TabLink/actions/runs/36927438600) independently passed the same jobs for final release commit `2a1c3aced048315e3171489fc410c6adeb2eed66`. Annotated tag `v0.8.9-preview.1` points exactly to that commit.

## Android installation compatibility and frame-rate evidence

The ADB compatibility page first reads the current Android foreground user. Installation targets that user explicitly. Each USB display session separately pins the current user so capability Provider access, the first Activity start and reconnection use one session user. If the foreground user changes, recovery stops before checking or rebuilding a reverse route; it does not switch users silently.

Installation uses Google Platform-Tools with `--no-streaming`: it transfers the APK first and then asks the package manager to commit it. Success is shown only when ADB returns a standalone `Success` line. This avoids a manufacturer installer displaying a result screen during a streaming transaction while never returning a final result to ADB. A timeout or ambiguous response remains a failure and does not trigger a second installation transaction. Every command remains bound to the one selected USB device, with Windows USB identity, ADB state and exclusion rules rechecked before execution.

The physical-presentation measurement tool also fixes field drift against `session-health.json`. 0.8.9 records SurfaceFlinger actual-present physical presentation, Windows presentation-callback deltas, Android decode submission and Android presentation callbacks separately; the latter three cannot replace physical presentation. A domain-separated SHA-256 of the device serial binds ADB session health to the measurement target, while measurement JSON never stores the raw serial. The cross-language C# / PowerShell rule and field mapping are enforced by the root build and CI.

## W202DS core device results

The only authorised W202DS was upgraded in place from 0.8.8 / build 20 to 0.8.9 / build 21 while retaining data. After protected USB session hand-off was added, the final candidate was installed in place again with the same pinned Preview signer. The last transaction used `--no-streaming -r` and returned a standalone `Success`; the first-install time stayed unchanged and the update time advanced. The APK read back from the device is 346,098 bytes with SHA-256 `34DB1B9F2FD808D8BA7958F1744AA8915677F93FA7DABD820A62C86823F07C7D`. It is byte-identical to the final-commit build and the public GitHub download. The installed package state also contains the new protected Provider.

The ADB-compatible connection created one TabLink VDD and one Android video Surface. The W202DS reported landscape logical size 1920 × 1200, native size 1200 × 1920, rotation 1/4 turn, and support for 60 / 90 Hz; current and requested refresh were both 90 Hz. NVENC hardware encoding requested and sustained an effective 90 fps. Moving the display by 100 pixels along the adjacent edge and moving it back kept the same session connected, with frame counts continuing and one VDD and one Surface throughout. Within about 7.205 seconds of exiting TabLink, the VDD count fell from 1 to 0 and active desktops from 2 to 1. ToDesk's virtual display adapter was unaffected.

A continuous 30.115-second SurfaceFlinger sample measured 86.9661 fps physical actual-present. In the same final sample, the Windows presentation callback, Android decode submission and Android presentation callback measured 90.1825, 89.9522 and 90.0041 fps. P95 / P99 / maximum intervals were 11.141 / 22.220 / 33.346 ms, with zero coverage gaps. Metric definitions and raw-evidence boundaries are documented in the [0.8.9 verification record](VERIFICATION-0.8.9.md).

After the new APK was installed, two consecutive cycles of “start the Windows candidate → receive real presentation → exit normally” were completed without force-stopping, clearing or reinstalling the Android app between cycles. Both used ADB at 1920 × 1200 / 90 Hz, created one TabLink VDD, and continued to increase sent and presented frame counts through a 12-second stability window. The second cycle took over successfully from an Android Session still retrying the old endpoint. After each exit, the Windows process, TabLink VDD count and reverse-mapping count for that device returned to zero.

Device tests for one-time token replay, QR rotation, per-connection challenge values, active revocation, a complete computer restart, real Wi-Fi / USB-tethering route migration and a separate injected failure through the UI **Stop connection** action remain incomplete and are not part of this Preview's device-pass claim. Offline state-machine tests do not replace these device boundaries. The redacted support bundle has passed a real disconnected Windows UI snapshot, which does not prove connected display and video fields in the UI export.

## Compatibility and invariants

- The transport protocol major version remains v1. Registered devices still use the pinned computer certificate, an Android Keystore P-256 identity and a fresh challenge for every connection.
- Only one receiver and one TabLink virtual display may be active at a time. 0.8.9 does not add a third or fourth display.
- This release does not change the Android decoder, FFmpeg helper, display driver or encoding algorithm, and makes no new frame-rate promise. Requested Hz, decode submission, presentation callbacks and physical panel presentation remain distinct measurements.
- Minimum Android remains API 23. Native Wi-Fi and USB tethering do not require ADB. The USB-debugging compatibility path accepts only the pinned Google Platform-Tools r37.0.0 three-file set.
- The public Windows application still has no Authenticode code signature, so Windows shows an unknown publisher at start-up. Existing signature and system-policy boundaries for the bundled driver are unchanged.
- The public package does not include Google ADB binaries or the browser WebRTC dependency whose redistribution is geographically restricted. Native Android receiving is unaffected.

## Public release assets

[GitHub Release `v0.8.9-preview.1`](https://github.com/linjierd/TabLink/releases/tag/v0.8.9-preview.1) is published as a prerelease. Neither `latest` nor the signed `stable` update channel was advanced.

| Asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `TabLink-Windows-x64-0.8.9-preview.1.zip` | 97,320,509 | `4448CCA4151247F535139DBEC5A9D62331E9EB66E89491E0AEB47481D2726206` |
| `TabLink-Android-0.8.9-preview.1.apk` | 346,098 | `34DB1B9F2FD808D8BA7958F1744AA8915677F93FA7DABD820A62C86823F07C7D` |
| `TabLink-FFmpeg-7.0.2-corresponding-source.tar.gz` | 28,919,316 | `FD7977F53EDD262D55C49F200EB5F54B1B12F5FFA547770380448708D75EA6F2` |
| `SHA256SUMS.txt` | 326 | `D120A47FA25634F6F8AC33071D5039FB4339A3AE9E6D4EAEC6D9EBAD00B07EA5` |

All four files were downloaded from the Release's public HTTPS addresses into a fresh E-drive directory and verified. The first three match `SHA256SUMS.txt`, and the checksum file itself matches its pre-release pinned hash. The downloaded Windows ZIP contains 463 files; its internal manifest covers the other 462. Every path, size and SHA-256 matches the PublicRelease from final commit `2a1c3aced048315e3171489fc410c6adeb2eed66`. The packaged `TabLink.exe` has FileVersion `0.8.9.0`, ProductVersion `0.8.9+2a1c3aced048315e3171489fc410c6adeb2eed66` and SHA-256 `CAA9678795270275B0EB5CA2439C6F2C00C342DA93A7FA8E58AFE8184D167C3B`. The ZIP contains no PDB, Google ADB three-file set or SIPSorcery binary.
