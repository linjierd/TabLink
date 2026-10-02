# TabLink Device Compatibility Catalogue

**English (Singapore)** | [简体中文](README.zh-CN.md)

This catalogue is generated from `compatibility/catalog.json`; do not edit it by hand. Schema v1; 2 reviewed records.
It contains only manually reviewed, non-unique device model and capability information.
Each record demonstrates only the exact software, hardware and connection configuration shown in the table. It does not establish compatibility for other operating-system versions or connection methods on the same model.

A requested refresh rate is distinct from decode submission, presentation callback and physical presentation rates. An unmeasured physical presentation rate is shown as `—`.
The catalogue does not accept automatically collected telemetry, automatic imports from issues or support bundles, device serial numbers, network addresses, USB identifiers or pairing credentials. Automated validation cannot replace a maintainer's review of the public model information and test conclusion.
After changing the source data, run `dotnet run --project tools/TabLink.CompatibilityCatalog/TabLink.CompatibilityCatalog.csproj -c Release -- --root . --write`, then commit the source data, schema and both language versions of this page.

## Reviewed records

| Date | Result | Host | Receiver | Connection | Display | Refresh rates | Video | Evidence |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-10-02 | `verified` | Windows 11 / `x64` / NVIDIA RTX 4060 Laptop GPU | `android` / `android-native` / ZTE W202DS / Android 13 / c2.unisoc.avc.decoder | `native-network` | 1920×1200 (native 1200×1920; rotation 1/4 turn) | 90 Hz (requested 90 Hz; supported 60/90 Hz) | `h264` / `nvenc` / `hardware`; presentation callback 90 fps; physical presentation — | [VERIFICATION-0.8.8.md](../docs/VERIFICATION-0.8.8.md) |
| 2026-10-02 | `verified` | Windows 11 / `x64` / NVIDIA RTX 4060 Laptop GPU | `android` / `android-native` / ZTE W202DS / Android 13 / unknown | `adb` | 1920×1200 (native 1200×1920; rotation 1/4 turn) | 90 Hz (requested 90 Hz; supported 60/90 Hz) | `h264` / `nvenc` / `unknown`; presentation callback 90.004 fps; physical presentation 86.966 fps (`surfaceflinger`) | [VERIFICATION-0.8.9.md](../VERIFICATION-0.8.9.md) |

## Capabilities and limitations

### `tlc-000001`

- TabLink: `0.8.8` / `preview` / `v0.8.8-preview.1`
- Source: `maintainer-verification`; commit `7c20a72c5d77cd454a4115d4a763d668f002f329`
- Video measurements: requested 90 fps; effective 90 fps; submitted 90.1 fps; presentation callback 90 fps; physical presentation —
- Verified: `app-process-restart-reconnect`, `extended-desktop`, `hardware-decoding`, `native-orientation`, `ninety-hz`, `single-display-cleanup`, `trusted-reconnect`, `trusted-registration`
- Limitations: `active-revocation-not-tested`, `route-migration-not-tested`, `system-restart-not-tested`, `token-replay-not-tested`

### `tlc-000002`

- TabLink: `0.8.9` / `preview` / `v0.8.9-preview.1`
- Source: `maintainer-verification`; commit `2a1c3aced048315e3171489fc410c6adeb2eed66`
- Video measurements: requested 90 fps; effective 90 fps; submitted 89.952 fps; presentation callback 90.004 fps; physical presentation 86.966 fps (`surfaceflinger`)
- Verified: `extended-desktop`, `native-orientation`, `ninety-hz`, `single-display-cleanup`
- Limitations: `system-restart-not-tested`, `usb-debug-required`
