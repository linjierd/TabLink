# TabLink bundled Windows ADB

This directory contains an unmodified, pinned Windows ADB subset from Google's official Android SDK Platform-Tools **37.0.0**. The ADB executable reports **1.0.41 / 37.0.0-14910828**. The version matches the already-installed official SDK on the development machine; preparing this bundle does not update or replace that SDK or its running ADB server.

Included runtime files:

- `bin/adb.exe`
- `bin/AdbWinApi.dll`
- `bin/AdbWinUsbApi.dll`

The package does not include fastboot, filesystem tools, USB drivers, or device firmware. All three runtime files were independently matched to the fixed official Google archive and have valid Google LLC Authenticode signatures. `adb version` was the only executed ADB command during preparation; no device command or server start/stop was run.

## Provenance and repeatable preparation

- Official release information: <https://developer.android.com/tools/releases/platform-tools>
- Fixed archive: <https://dl.google.com/android/repository/platform-tools_r37.0.0-win.zip>
- Archive size: 8,092,164 bytes
- Archive SHA-256: `4FE305812DB074CEA32903A489D061EB4454CBC90A49E8FEA677F4B7AF764918`
- `provenance.json` records the archive, component hashes and signature information.
- `SHA256SUMS.txt` covers runtime files and accompanying license/provenance files.

From the repository root:

```powershell
.\tools\Prepare-BundledAdb.ps1
.\tools\Prepare-BundledAdb.ps1 -VerifyOnly
```

Normal preparation first accepts an already-valid bundle, then a matching installed SDK, then the fixed official download. `-VerifyOnly` never downloads or executes anything; it checks the pinned hashes and the complete accompanying notices. To explicitly rehydrate from the official archive, use `-Source OfficialDownload`. Files from another ADB version are rejected instead of being mixed with this package. The downloaded full archive stays in `.cache` and is not part of the TabLink release.

## Release layout

Copy all three files from `bin` to **`tools/platform-tools`** below the TabLink executable. Keep `LICENSE.txt`, `NOTICE.txt`, `source.properties`, `README.md`, `provenance.json` and `SHA256SUMS.txt` with the release's ADB notices. The notice files must accompany the binaries, not remain only in the source checkout.

Runtime discovery preserves an explicitly configured complete ADB installation. With no explicit path, it prefers this bundled installation before `ANDROID_SDK_ROOT`, `ANDROID_HOME`, the per-user Android SDK, and PATH. Discovery requires nonempty `adb.exe`, `AdbWinApi.dll`, and `AdbWinUsbApi.dll` files. A missing/invalid explicitly configured installation returns an error to its caller instead of silently switching tools. Discovery does not run ADB, change PATH, install drivers or modify USB configuration.

## License scope and redistribution limits

`LICENSE.txt` is the Apache License 2.0 section copied from the official archive's notices. `NOTICE.txt` is the **complete unmodified upstream Platform-Tools notice file**, retaining additional copyright and license terms; the complete SDK notice includes components outside this minimal three-file subset as well.

Google's [Android SDK License Agreement](https://developer.android.com/studio/terms) section 3.4 generally restricts redistribution of the SDK, while section 3.5 says open-source components are governed by their respective open-source licenses. Therefore, the presence of an Apache license text must not be interpreted as a blanket license for the full SDK or every linked dependency. Preserve the upstream notices and comply with the licenses applicable to the actual distributed components; those can include source-code or relinking obligations where relevant. This task prepares the user's requested local runtime bundle and verifies provenance; it is not a completed dependency-by-dependency legal/source-compliance audit or public redistribution clearance. Google trademarks and endorsement rights are not granted by bundling these tools.

ADB source reference: <https://android.googlesource.com/platform/packages/modules/adb/>

Do not remove or alter the upstream notice text or rebrand these binaries as TabLink's own implementation.
