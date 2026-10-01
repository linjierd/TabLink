# Third-party notices

The MIT license in [`LICENSE`](LICENSE) covers code authored for TabLink. It
does not replace the licenses of third-party packages, drivers or tools.

| Component | Version / source | License and distribution note |
| --- | --- | --- |
| Virtual Display Driver | VirtualDrivers 25.7.23 | MIT. The pinned, signed upstream driver and its exact license are retained under `third_party/VirtualDisplayDriver/`. |
| FFmpeg hardware helper | 7.0.2, TabLink patch set | `ffmpeg.exe` is configured LGPL-2.1-or-later and provides NVENC, QSV and AMF. Patch, build scripts, source provenance and final hashes belong under `third_party/ffmpeg-tablink/`. A binary release must provide complete corresponding source and the LGPL license. |
| FFmpeg libx264 helper | 7.0.2, TabLink patch set | `ffmpeg-x264.exe` enables GPL libx264 and is therefore distributed under GPL-2.0-or-later. It remains a separate helper and is used only after explicit user authorization. A binary release must preserve the GPL license and complete corresponding source for FFmpeg and x264. |
| nv-codec-headers | 12.2.72.0 | Permissive NVIDIA notice carried by the headers; used to compile NVENC support in the hardware helper. Preserve the exact upstream notices in the source bundle and binary notices. |
| oneVPL | 2.11.0 | MIT; headers and Windows loader used to compile Intel QSV support in the hardware helper. Preserve the upstream license, `third-party-programs.txt` notices and corresponding source. |
| AMD AMF | 1.4.35 | MIT with the upstream media-technology/patent notice; headers used to compile AMD AMF support in the hardware helper. Preserve `LICENSE.txt` in the corresponding source and notices. |
| x264 | stable commit `b35605ace3ddf7c1a5d67a2eb553f034aef41d55` | GPL-2.0-or-later; statically used only by the separate `ffmpeg-x264.exe` helper. Preserve the exact commit identity, COPYING and source in the corresponding source bundle. |
| QRCoder | 1.6.0 | MIT; restored from NuGet. The upstream notice is under `third_party/qrcoder/`. |
| ZXing Core | 3.5.3 | Apache-2.0; restored from Maven Central by the Android build. |
| SIPSorcery | 10.0.16 | The upstream package uses BSD-3-Clause **plus an additional geographic/use restriction**. That restriction means the package is not an OSI-approved open-source dependency. Read `third_party/sipsorcery/10.0.16/LICENSE.md` and `NOTICE.md` before building or redistributing the browser receiver. |
| BouncyCastle.Cryptography | 2.7.0 | MIT; transitive SIPSorcery dependency. |
| Concentus | 2.2.2 | BSD-3-Clause; transitive SIPSorcery dependency. |
| Android Debug Bridge | Platform-Tools 37.0.0 | The public source repository does not contain ADB executables. `third_party/adb/` keeps provenance, hashes and upstream notices. Google's Android SDK terms may restrict redistribution; obtain Platform-Tools from Google for local builds. |

The dependency graph recorded by NuGet or Gradle remains authoritative for a
particular build. Anyone publishing binaries is responsible for preserving all
applicable notices and satisfying corresponding-source or relinking duties.

For 0.8.4 Preview 1, the final E-drive hardware helper passed two NVIDIA NVENC
1200 x 1920 @ 90 probes at 125.6-127.6 fps. Forced QSV failed closed on this
host because no supported MFX implementation was available (`-9`); forced AMF
failed closed because this non-AMD host had no `amfrt64.dll`. Neither failure
changed backend. These results verify failure isolation on the current host,
not QSV or AMF operation on compatible Intel or AMD hardware; those device
validations remain pending.

Final 0.8.4 delivery SHA-256 values are: `ffmpeg.exe`
`F47DA86A069F8F8EB30BCF42CE6137962691A9A1197386D262646A33D3D62659`,
`ffmpeg-x264.exe`
`B4C34236895D986C4ED452949515768348972DF1DC2FE8B85E81FEFEEA663EBE`,
and `source-bundle.tar.gz`
`C59D8F6D6B5FD02505D36714967183747010EE128FA29F9D967550BFCAE08D30`.

The browser receiver currently depends on SIPSorcery 10.0.16. Consequently,
the TabLink-authored source is MIT licensed, while a combined binary that
includes that dependency is subject to its additional upstream restriction.
