# TabLink's private Windows FFmpeg builds

This directory contains two isolated FFmpeg 7.0.2 builds for TabLink's one selected virtual display. They do not replace an installed FFmpeg or install/update any GPU driver. The hardware output is `bin/ffmpeg.exe`, configured LGPL-2.1-or-later with NVENC, Intel QSV and AMD AMF. The separate software output is `bin/ffmpeg-x264.exe`, configured GPL-2.0-or-later with libx264. The staged source carries TabLink's Windows capture-timing patches; both helpers contain no network protocols.

TabLink probes an encoder at the exact target size and frame rate before starting media. Automatic selection prefers hardware associated with the target output and remains sticky for that connection, including capture recovery and adaptive bitrate rebuilds. The x264 helper is never an implicit default: the user must explicitly allow software fallback, and its effective video target is capped at 30 fps. Encoder selection does not change the global one-virtual-display limit.

## Change

`0001-windows-private-high-resolution-usleep.patch` changes only `libavutil/time.c`. On Windows, `av_usleep` creates a private high-resolution waitable timer (`CreateWaitableTimerExW`, documented flag `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`), schedules one relative wait, waits without busy-spinning, and closes its handle on every path. Unsupported or failed timer creation falls back to FFmpeg's prior `Sleep(usec / 1000)`. A zero delay retains `Sleep(0)` semantics.

`0002-ddagrab-nonblocking-duplicate.patch` changes only `libavfilter/vsrc_ddagrab.c`. The existing request-frame clock still paces output at the configured frame rate. After `ddagrab` has one valid cached frame and `dup_frames=1`, `AcquireNextFrame` uses a zero timeout so an unchanged desktop can reuse that frame immediately instead of spending up to half a frame waiting before the fallback. Initial format probing, the first valid frame, `dup_frames=0`, and DDA error recovery keep the original bounded wait. The 0.8.5 helpers include this patch; final hashes and real-device results are recorded in `VALIDATION.md`.

The Windows branch deliberately precedes `HAVE_NANOSLEEP` and `HAVE_USLEEP`, both of which are detected by this MinGW toolchain. No `timeBeginPeriod`, `NtSetTimerResolution`, registry change, administrator operation, DLL injection, or process-wide timer-period change is used. Per-call ownership avoids a leaked per-thread timer when a capture thread exits. Microsoft documents the high-resolution flag as supported since Windows 10 version 1803:
https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-createwaitabletimerexw

## Fixed sources and provenance

* FFmpeg official source archive: https://ffmpeg.org/releases/ffmpeg-7.0.2.tar.xz
  SHA-256: `8646515b638a3ad303e23af6a3587734447cb8fc0a0c064ecdb8e95c4fd8b389`
  Official tag `n7.0.2`, commit `e3a61e91030696348b56361bdf80ea358aef4a19`:
  https://github.com/FFmpeg/FFmpeg/tree/e3a61e91030696348b56361bdf80ea358aef4a19
* NVIDIA codec interface headers: upstream FFmpeg project tag `n12.2.72.0`, commit `c69278340ab1d5559c7d7bf0edf615dc33ddbba7`:
  https://github.com/FFmpeg/nv-codec-headers/tree/c69278340ab1d5559c7d7bf0edf615dc33ddbba7
  Archive: https://github.com/FFmpeg/nv-codec-headers/archive/refs/tags/n12.2.72.0.tar.gz
  SHA-256: `dbeaec433d93b850714760282f1d0992b1254fc3b5a6cb7d76fc1340a1e47563`
  This fixes NVENC API compatibility to 12.2; newer system packages require APIs unsupported by this machine's driver.
* Intel oneVPL: upstream tag `v2.11.0`, commit `11a9bbda5b22ac1c544da59b4007bb57f737b487`, MIT. Its headers and Windows loader build provide QSV support. `downloads-manifest.json` records the frozen archive size and SHA-256. Preserve both `LICENSE` and `third-party-programs.txt`; the latter is copied beside the hardware helper as `COPYING.oneVPL-third-party-programs.txt`.
* AMD Advanced Media Framework: upstream tag `v1.4.35`, commit `ba07d1b3c7c7ee0d846125cffc1d6d78a020c45a`, MIT plus the upstream media-technology/patent notice. Its headers provide AMF support. `downloads-manifest.json` records the frozen archive size and SHA-256; the source bundle preserves `LICENSE.txt`.
* x264: fixed stable commit `b35605ace3ddf7c1a5d67a2eb553f034aef41d55`, GPL-2.0-or-later. It is built only into `ffmpeg-x264.exe`; the LGPL hardware helper does not link it. Preserve the complete commit identity, `COPYING`, archive or Git provenance, and source in the final source bundle metadata.
* Portable compiler: official w64devkit 2.0.0, GCC 14.2.0:
  https://github.com/skeeto/w64devkit/releases/tag/v2.0.0
  Archive: https://github.com/skeeto/w64devkit/releases/download/v2.0.0/w64devkit-x64-2.0.0.exe
  SHA-256: `cea23fc56a5e61457492113a8377c8ab0c42ed82303fcc454ccd1963a46f8ce1`
  Toolchain source bundle is published with that release as `source.tar`.

`downloads-manifest.json` records the frozen official/archive sizes and hashes used by the final build, plus the exact x264 Git commit because that upstream does not provide a stable release archive for this commit. Detached signatures for the FFmpeg archive and the w64devkit executable previously verified successfully using keys downloaded from their official project sites, in an isolated temporary GPG keyring. `signature-verification.log` records that historical result. Only the temporary keyring directory in that log is represented as `[isolated-temporary-keyring]`; the GPG results, signer identities, warnings, and fingerprints are otherwise unchanged. The generated H.264 output location in `production-parser-test.log` is similarly represented beneath `[isolated-temporary-output]`, without changing its measurements or PASS results. Key fingerprints:

* FFmpeg: `FCF986EA15E6E293A5644F10B4322F04D67658D8` (https://ffmpeg.org/ffmpeg-devel.asc).
* w64devkit: `5EEB8C8D5069C4E9B94AA852AFD1503A8C8FF42A` (https://github.com/skeeto.gpg).

The signatures establish consistency with those published keys; no user-keyring trust settings were changed. The nv-codec-headers archive is pinned by official tag/commit and a locally recorded SHA-256; it has no claimed independent release signature.

## Build

The patched complete FFmpeg source is staged under `source/ffmpeg-7.0.2`. A final 0.8.5 build also stages the fixed nv-codec-headers, oneVPL, AMF and x264 sources. The pristine official archives or exact Git identities and a reviewable unified patch are retained alongside them. Header sources, built dependency libraries and the extracted portable compiler remain build inputs, not substitutes for corresponding source.

Run `powershell -File ./build.ps1` from this directory. The wrapper stages fresh ASCII-only work directories on the E drive because GCC 14's linker cannot reliably resolve this repository's Chinese path. The 0.8.5 release process must set `TEMP`, `TMP` and `TMPDIR` to that E-drive staging area for dependency and FFmpeg configure/build steps. A binary whose build log shows the user's system TEMP is probe-only and must not be distributed. The wrapper changes only child-process environment, restores its own shell environment afterward, rejects results containing drive-qualified `Users` paths, and copies the two completed executables into `bin`.

The distributable `source-bundle.tar.gz` includes complete corresponding source for patched FFmpeg, nv-codec-headers, oneVPL, AMF and x264; it also includes patches, scripts, notices, licenses, provenance, signature evidence and validation records. It excludes both helper executables and the compiler binary. Before copying the bundle, the public release build verifies both helpers and the source archive against the tracked `SHA256SUMS`, requires all corresponding-source and license entries, and rejects absolute paths, `..` traversal, case-conflicting/link entries and extracted content containing drive-qualified or MSYS-style personal `Users` paths. After unpacking into a fresh directory, follow the bundled toolchain preparation and build instructions. Runtime helpers may import Windows system DLLs and dynamically use the applicable GPU driver runtime; the x264 helper has no GPU requirement.

`build.sh` contains the exact configure options. It uses the neutral compile-time prefixes `/ffmpeg-tablink-085-hardware` and `/ffmpeg-tablink-085-software`, so `ffmpeg -version` and compiled data-directory defaults do not reveal the builder's account name or checkout path. These prefixes are not installed runtime dependencies for the minimal builds. The linker receives `--no-insert-timestamp`, and `SOURCE_DATE_EPOCH=0` also makes the final GNU strip step deterministic; clean builds from different new staging directories must therefore produce the recorded executable hash. The script uses the GNU awk already included with Git for Windows (`C:/Program Files/Git/usr/bin/awk.exe`, overridable through `TABLINK_GNU_AWK`) while running configure, because the old busybox awk bundled with w64devkit 2.0.0 incorrectly evaluates configure's ternary expressions. `--pkg-config-flags=--dont-define-prefix` preserves the explicitly selected local codec-header path.

The hardware helper includes D3D11 Desktop Duplication (`ddagrab`), selected-area GDI capture (`gdigrab`) and the H.264 NVENC/QSV/AMF encoders, plus only the muxers, file/pipe protocols, synthetic source and filters needed for production and validation. It excludes libx264, GPL components and network protocols. The software helper includes libx264 and the corresponding minimal capture/test components, excludes network protocols and is distributed under GPL-2.0-or-later. Existing TabLink display-identity validation and selected-display bounds remain mandatory; neither helper chooses or creates a display.

## Licenses and distribution

The hardware configure excludes GPL, version3 and nonfree features and reports **LGPL version 2.1 or later**. Preserve `COPYING.LGPLv2.1`, complete corresponding source, dependency licenses, this patch and build instructions when distributing `ffmpeg.exe`.

The software configure enables GPL and libx264 and reports **GPL version 2 or later**. Preserve `COPYING.GPLv2`, x264 `COPYING`, complete FFmpeg/x264 corresponding source, this patch and build instructions when distributing `ffmpeg-x264.exe`. TabLink launches each helper as a separate process.

The NV codec headers carry NVIDIA's permissive notice in each header; the binary notices preserve it. oneVPL is MIT, and its upstream `third-party-programs.txt` notices accompany the hardware helper. AMF is MIT and includes an upstream standards/media-technology notice that must remain intact. The portable compiler's runtime notice must accompany both helpers. The compiler itself and Git GNU awk are build tools, not application runtime dependencies.

## Validation artifacts

`timer-probe.c` links directly to the built `libavutil/time.o` and compares the actual patched `av_usleep` against original Windows Sleep, including handle-count checks. Every final helper must also pass the production runtime probe: exact target dimensions, bounded target frames, monotonic PTS, valid SPS/PPS/IDR, no B frames, exactly one AUD per access unit, and minimum throughput. Synthetic encoding and dynamic desktop tests remain separate: synthetic throughput does not establish unique desktop frames, and an `fps` filter's duplicated frames do not count as genuine motion capture.

See `VALIDATION.md` for the distinction between final synthetic encoder evidence, hardware unavailable on this host, and real desktop/tablet validation. `motion-085-final-result.json` preserves the sanitized final-helper barcode result; no unchanged or repeated desktop frame is counted as a newly presented source frame. The final E-drive helpers are `ffmpeg.exe` SHA-256 `BB1FA5F2A5CC572C6A1D310F88348324EE43B84DF5A778FD0AF02D77B3C86627` and `ffmpeg-x264.exe` SHA-256 `6E3EA733AD40DA6D6D78C2DFC51BCCA950C3519D3304AE045316F7D55B89EDB7`; the generated source archive hash is published separately in component and release manifests to avoid a circular self-hash.
