# TabLink's private Windows FFmpeg build

This directory is an isolated FFmpeg 7.0.2 build for TabLink's selected virtual display. It does not replace the installed FFmpeg, NVIDIA driver, or the existing Gyan 7.0.2 package. The output is `bin/ffmpeg.exe`, with version suffix `tablink-hires1`.

## Change

`0001-windows-private-high-resolution-usleep.patch` changes only `libavutil/time.c`. On Windows, `av_usleep` creates a private high-resolution waitable timer (`CreateWaitableTimerExW`, documented flag `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`), schedules one relative wait, waits without busy-spinning, and closes its handle on every path. Unsupported or failed timer creation falls back to FFmpeg's prior `Sleep(usec / 1000)`. A zero delay retains `Sleep(0)` semantics.

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
* Portable compiler: official w64devkit 2.0.0, GCC 14.2.0:
  https://github.com/skeeto/w64devkit/releases/tag/v2.0.0
  Archive: https://github.com/skeeto/w64devkit/releases/download/v2.0.0/w64devkit-x64-2.0.0.exe
  SHA-256: `cea23fc56a5e61457492113a8377c8ab0c42ed82303fcc454ccd1963a46f8ce1`
  Toolchain source bundle is published with that release as `source.tar`.

`downloads-manifest.json` records the local archive sizes/hashes. Detached signatures for the FFmpeg archive and the w64devkit executable verified successfully using keys downloaded from their official project sites, in an isolated temporary GPG keyring. `signature-verification.log` records the result. Key fingerprints:

* FFmpeg: `FCF986EA15E6E293A5644F10B4322F04D67658D8` (https://ffmpeg.org/ffmpeg-devel.asc).
* w64devkit: `5EEB8C8D5069C4E9B94AA852AFD1503A8C8FF42A` (https://github.com/skeeto.gpg).

The signatures establish consistency with those published keys; no user-keyring trust settings were changed. The nv-codec-headers archive is pinned by official tag/commit and a locally recorded SHA-256; it has no claimed independent release signature.

## Build

The patched complete FFmpeg source is under `source/ffmpeg-7.0.2`. The pristine official archive and a reviewable unified patch are retained alongside it. Header sources and the extracted portable compiler are also local.

Run `powershell -File ./build.ps1` from this directory. The wrapper stages a fresh ASCII-only directory under the current user's temporary directory because GCC 14's linker cannot reliably resolve this repository's Chinese path. It changes only the child build environment's PATH and restores its own shell environment afterward. It leaves the staged objects/configuration for inspection and copies the completed executable into `bin`.

The distributable `source-bundle.tar.gz` includes the complete corresponding patched FFmpeg and codec-header sources, patch, scripts, notices, and validation records. It excludes the compiler binary. After unpacking that bundle into a fresh directory, run `powershell -File ./prepare-toolchain.ps1` to download the fixed official compiler archive, verify its recorded SHA-256 before execution, and extract it only into the local `toolchain` folder; then run `build.ps1`. Git for Windows with GNU awk is the other build prerequisite. The runtime executable has only Windows system DLL imports, plus the GPU driver APIs it loads dynamically.

`build.sh` contains the exact configure options. It uses the GNU awk already included with Git for Windows (`C:/Program Files/Git/usr/bin/awk.exe`, overridable through `TABLINK_GNU_AWK`) while running configure, because the old busybox awk bundled with w64devkit 2.0.0 incorrectly evaluates configure's ternary expressions. `--pkg-config-flags=--dont-define-prefix` preserves the explicitly selected local codec-header path.

The minimal build includes D3D11 Desktop Duplication (`ddagrab`), selected-area GDI capture (`gdigrab`), NVENC H.264, H.264/rawvideo/null muxers, file/pipe protocols, synthetic `testsrc2`, and the format/scale/hwdownload/fps/crop filters needed for validation. It includes no network protocols or dependency on libx264. Existing TabLink display-identity validation and selected-display bounds remain mandatory; this binary does not choose a display itself.

## Licenses and distribution

This configure excludes GPL, version3, and nonfree features and reports **LGPL version 2.1 or later**. Preserve `COPYING.LGPLv2.1`, the corresponding complete source, this patch and build instructions when distributing the modified executable. TabLink launches it as a separate process.

The NV codec headers carry NVIDIA's MIT-style permissive notice in each header; `bin/COPYING.NVIDIA.txt` preserves the encoder header's notice. The portable compiler's runtime notice `bin/COPYING.MinGW-w64-runtime.txt` must accompany the binary. The compiler itself and Git GNU awk are build tools, not application runtime dependencies.

## Validation artifacts

`timer-probe.c` links directly to the built `libavutil/time.o` and compares the actual patched `av_usleep` against original Windows Sleep, including handle-count checks. Synthetic encoding and dynamic desktop tests must be reported separately: synthetic throughput does not establish unique desktop frames, and an `fps` filter's duplicated frames do not count as genuine motion capture.

See `VALIDATION.md` for the distinction between the verified timer/encoding improvement and the remaining unique-motion-frame investigation. No unchanged or repeated desktop frame should be counted as a newly presented source frame.
