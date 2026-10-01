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

The TabLink FFmpeg patch set contains `0001`, which replaces the private
Windows capture wait with a per-call high-resolution waitable timer, and
`0002`, which makes the cached-frame `ddagrab` query nonblocking only when
`dup_frames=1`. The request-frame clock still paces output, and first-frame,
probe and recovery paths retain the original bounded wait. Both patches and
the complete corresponding source are included with the public package.

For 0.8.5 Preview 1, the final E-drive hardware helper delivered all 450
synthetic NVIDIA NVENC 1200 x 1920 @ 90 frames at 170.17 fps encoder
throughput. Its bounded dynamic-desktop barcode run encoded 899 frames with
898 different source IDs, or 89.746 different source pictures/s. Forced QSV failed closed on this
host because no supported MFX implementation was available (`-9`); forced AMF
failed closed because this non-AMD host had no `amfrt64.dll`. Neither failure
changed backend. These results verify failure isolation on the current host,
not QSV or AMF operation on compatible Intel or AMD hardware; those device
validations remain pending.

Final 0.8.5 delivery SHA-256 values are: `ffmpeg.exe`
`BB1FA5F2A5CC572C6A1D310F88348324EE43B84DF5A778FD0AF02D77B3C86627`,
`ffmpeg-x264.exe`
`6E3EA733AD40DA6D6D78C2DFC51BCCA950C3519D3304AE045316F7D55B89EDB7`,
and `source-bundle.tar.gz`
`FD7977F53EDD262D55C49F200EB5F54B1B12F5FFA547770380448708D75EA6F2`.

The browser receiver currently depends on SIPSorcery 10.0.16. Consequently,
the TabLink-authored source is MIT licensed, while a combined binary that
includes that dependency is subject to its additional upstream restriction.
