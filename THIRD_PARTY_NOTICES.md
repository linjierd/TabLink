# Third-party notices

The MIT license in [`LICENSE`](LICENSE) covers code authored for TabLink. It
does not replace the licenses of third-party packages, drivers or tools.

| Component | Version / source | License and distribution note |
| --- | --- | --- |
| Virtual Display Driver | VirtualDrivers 25.7.23 | MIT. The pinned, signed upstream driver and its exact license are retained under `third_party/VirtualDisplayDriver/`. |
| FFmpeg | 7.0.2, TabLink patch set | LGPL-2.1-or-later build. Patch, build scripts, source URL and hashes are under `third_party/ffmpeg-tablink/`. Generated binaries, downloaded source trees and toolchains are not committed. A binary release must provide the corresponding source bundle and license. |
| QRCoder | 1.6.0 | MIT; restored from NuGet. The upstream notice is under `third_party/qrcoder/`. |
| ZXing Core | 3.5.3 | Apache-2.0; restored from Maven Central by the Android build. |
| SIPSorcery | 10.0.16 | The upstream package uses BSD-3-Clause **plus an additional geographic/use restriction**. That restriction means the package is not an OSI-approved open-source dependency. Read `third_party/sipsorcery/10.0.16/LICENSE.md` and `NOTICE.md` before building or redistributing the browser receiver. |
| BouncyCastle.Cryptography | 2.7.0 | MIT; transitive SIPSorcery dependency. |
| Concentus | 2.2.2 | BSD-3-Clause; transitive SIPSorcery dependency. |
| Android Debug Bridge | Platform-Tools 37.0.0 | The public source repository does not contain ADB executables. `third_party/adb/` keeps provenance, hashes and upstream notices. Google's Android SDK terms may restrict redistribution; obtain Platform-Tools from Google for local builds. |

The dependency graph recorded by NuGet or Gradle remains authoritative for a
particular build. Anyone publishing binaries is responsible for preserving all
applicable notices and satisfying corresponding-source or relinking duties.

The browser receiver currently depends on SIPSorcery 10.0.16. Consequently,
the TabLink-authored source is MIT licensed, while a combined binary that
includes that dependency is subject to its additional upstream restriction.
