# Virtual Display Driver — pinned offline dependency

Upstream: https://github.com/VirtualDrivers/Virtual-Display-Driver

Release: 25.7.23 (2025-07-23), retrieved and checked on 2026-09-19.

Asset: https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip

The upstream filename says x86, but its INF declares `Standard,NTamd64`; this is the x64 driver. It is not an ARM64 package. The INF's internal version is `12/24/2024,11.30.4.434`.

License: MIT, Copyright (c) 2024 Virtual Display. The exact upstream license is included in LICENSE.txt. No INF, CAT, or DLL content has been modified. The original upstream XML is preserved in driver/; the installer creates its own single-monitor XML only if no preexisting driver or configuration exists.

## SHA256

| File | SHA256 |
| --- | --- |
| VirtualDisplayDriver-x86.Driver.Only.zip | E24210692B442B39AF763536330CE78B423F19342B7A7792C26DE3944E418B3A |
| driver/MttVDD.inf | 550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D |
| driver/MttVDD.dll | C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05 |
| driver/mttvdd.cat | 08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6 |

The ZIP SHA256 also matched GitHub's release asset `digest` at retrieval.

## Signature verification recorded on the development machine

PowerShell `Get-AuthenticodeSignature` was run against both files without installing them:

```text
File: MttVDD.dll
Status: Valid
StatusMessage: Signature verified.
Signer: CN=SignPath Foundation, O=SignPath Foundation, L=Lewes, S=Delaware, C=US
Timestamp: CN=DigiCert Timestamp 2024, O=DigiCert, C=US

File: mttvdd.cat
Status: Valid
StatusMessage: Signature verified.
Signer: CN=SignPath Foundation, O=SignPath Foundation, L=Lewes, S=Delaware, C=US
Timestamp: CN=DigiCert Timestamp 2024, O=DigiCert, C=US
```

These are upstream SignPath signatures, not a claim of Microsoft WHQL certification. TabLink checks fixed hashes plus Windows WinVerifyTrust before invoking Windows driver installation. It does not install certificates, enable test signing, disable Secure Boot, or change signature enforcement. Trust verification uses local/cached certificates so the offline package has no mandatory download step; installation still depends on the receiving Windows system's trust and driver policies.

## Installation and ownership

`TabLink.DriverSetup.exe --install` requires UAC. It is only launched by the user's explicit installation button. The helper creates a root-enumerated `Root\MttVDD` device using SetupAPI and installs the exact verified INF using `UpdateDriverForPlugAndPlayDevices`. It checks all display nodes, including disabled/non-present ones, before creating a node. Existing MttVDD devices or existing `C:\VirtualDisplayDriver` configuration are not overwritten. A failure removes only the device node newly created by this attempt; an already staged Windows Driver Store package may remain. No other display driver is disabled or uninstalled. If new-node rollback fails, the user is informed and the configuration is retained.

The helper writes its result to `%LOCALAPPDATA%\TabLink\driver-install-result.json`. It does not change global display topology, the primary monitor, or physical monitor resolutions. The user can choose Extend in Windows display settings if Windows does not activate the new monitor automatically.

Installation reference: https://github.com/VirtualDrivers/Virtual-Display-Driver/wiki/Installation

Configuration reference: https://github.com/VirtualDrivers/Virtual-Display-Driver/wiki/How-to-configure-the-driver

Windows root-device installation reference: https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/devcon-install

This bundle has been downloaded, hash-checked, and signature-checked. Building TabLink does not install it; actual display creation and Android streaming need the user's hardware test.

Read-only integration validation also passed on the development machine: the helper's exact `ValidatePackage` implementation accepted the genuine package through its fixed SHA256 and WinVerifyTrust checks; a modified INF was rejected. SetupAPI reported no existing MttVDD node. CCD `GetDisplayConfigBufferSizes` and `QueryDisplayConfig` returned success with one existing physical-screen path, and `VirtualDisplayManager` correctly did not classify that screen as a compatible virtual secondary display. Native structure sizes were checked as 72 (path), 64 (mode), 420 (target name), 84 (source name), and 276 (adapter name) bytes. The elevated installation entry point was not executed.
