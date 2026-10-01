# Validation history and 0.8.4 multi-encoder status

## 0.8.4 Preview 1 candidate status (2026-10-01)

0.8.4 changes the runtime layout from one NVENC-only helper to two independent executables:

| File | Intended encoders | License configuration | Current validation status |
| --- | --- | --- | --- |
| `ffmpeg.exe` | `h264_nvenc`, `h264_qsv`, `h264_amf` | LGPL-2.1-or-later | Final E-drive helper. Two final Auto probes selected NVENC at 1200×1920@90 and measured 125.6–127.6 fps. Forced QSV failed closed because this host has no supported MFX implementation (`-9`); forced AMF failed closed because this non-AMD host has no `amfrt64.dll`. Neither failure changed backend. |
| `ffmpeg-x264.exe` | `libx264` | GPL-2.0-or-later | Final E-drive explicit-opt-in helper. Two final probes at the enforced 1200×1920@30 target measured 198.6–200.7 fps. The virtual display remained 90 Hz; only encoded frame rate was capped. |

Automatic selection is sticky for one connection: capture recovery and adaptive bitrate rebuilds reuse the selected helper/backend. A forced backend never silently falls back. A new connection probes again. These selector and argument invariants have automated coverage, but only a final packaged runtime probe establishes the released executable behavior.

Both helpers emit AVC without B frames. A single common `h264_metadata=aud=insert` bitstream filter supplies AUD boundaries; encoder-level AUD options are deliberately absent to prevent duplicate AUD NAL units. Final three-frame NVENC and x264 bitstreams each contained exactly three AUDs, one per access unit. The runtime probe also verified exact frame count, monotonically increasing PTS and SPS/PPS/IDR on the first access unit; no FFmpeg process remained afterward.

The complete corresponding source bundle for 0.8.4 must include patched FFmpeg 7.0.2, nv-codec-headers 12.2.72.0, oneVPL 2.11.0, AMF 1.4.35 and the fixed x264 stable source, with build scripts and licenses for both configurations. oneVPL's `third-party-programs.txt` is present in the source and copied beside the hardware helper as a binary-distribution notice. The release build, including dependency configure steps, must use E-drive `TEMP`, `TMP` and `TMPDIR`; earlier probe binaries whose configure logs refer to the user's system TEMP are not distributable.

Final E-drive helper identities:

* `ffmpeg.exe`: 3,463,680 bytes; SHA-256 `F47DA86A069F8F8EB30BCF42CE6137962691A9A1197386D262646A33D3D62659`.
* `ffmpeg-x264.exe`: 3,999,744 bytes; SHA-256 `B4C34236895D986C4ED452949515768348972DF1DC2FE8B85E81FEFEEA663EBE`.

The exact source-bundle hash is generated after this tracked validation record is embedded and is therefore published in `SHA256SUMS` outside the archive rather than recursively inside its own manifest.

## Historical NVENC-only validation (2026-09-20; privacy rebuild 2026-10-01)

Historical NVENC-only `bin/ffmpeg.exe`: 2,393,600 bytes; SHA-256 `A9B13FC5B5D287FD7EADB39C4755B84F6FEA44A7CE10DC8AC8BD7FFDA66FBBEC`.

The 2026-10-01 executable was rebuilt from the same patched FFmpeg 7.0.2 and nv-codec-headers 12.2 sources with the same feature configuration, compiler and LGPL status. Its compile-time installation prefix is the neutral `/ffmpeg-tablink`; ASCII and UTF-16 scans plus `ffmpeg -version` contain no drive-qualified `Users` path, builder account name or checkout path. `--no-insert-timestamp` plus `SOURCE_DATE_EPOCH=0` made independently built and stripped executables byte-identical at the hash above. The production `H264Encoder` test delivered all 450 synthetic 1200x1920@90 NVENC frames, validated Annex-B parsing and clock order, completed repeated disposal, and confirmed JobObject cleanup after abrupt owner exit. The rebuilt executable imports the same six Windows system DLLs listed below. The build wrapper and public release build now reject an FFmpeg binary containing a drive-qualified `Users` path. The corresponding source bundle preserves both successful GPG signature results while replacing only the temporary keyring directory with `[isolated-temporary-keyring]`; its parser test log likewise neutralizes only the generated temporary output location. The public release audit also rejects unsafe archive paths and personal `Users` paths in extracted content.

* The Windows timer branch is compiled even though `HAVE_NANOSLEEP=1` and `HAVE_USLEEP=1`; `HAVE_WINDOWS_H=1` and `HAVE_SLEEP=1` select it first.
* `timer-probe.c` linked to the actual built `libavutil/time.o`. Original Sleep median delays for requested 1/5/11.111 ms were 15.581/15.617/15.579 ms. Patched `av_usleep` medians were 1.546/5.303/11.453 ms. Handle counts remained 85 before and after 1,000 nonzero plus 1,000 zero waits. See `timer-probe-results.csv`.
* The same 64x64@90 lavfi/readrate stdout-arrival test changed median/P90 intervals from 15.167/16.009 ms to 11.075/11.349 ms. Above-14ms intervals changed from 224 to 0. The readrate test has an initial catch-up burst, so its whole-process average is not claimed as an end-to-end display rate. See `readrate-probe-results.json`.
* Production `H264Encoder` and `AnnexBParser` delivered all 450 synthetic 1200x1920@90 frames in 3.022 seconds (148.89 frames/s throughput). All parser/DXGI identity tests, concurrent repeated disposal, and abrupt-owner-exit JobObject cleanup passed: 11 tests. See `production-parser-test.log`.
* Independent FFprobe inspection: H.264 High, level 5.1, 1200x1920, no B frames, exactly 450 decoded frames. NVENC initialized using the existing RTX 4060 Laptop GPU driver; no driver installation or update occurred.
* A bounded selected-DISPLAY46 DDA90 motion test emitted 895 frames in 10 seconds with 9.59% of one CPU core. The WinForms source painted 90.06 unique frame numbers per second, but the encoded barcode showed only 650 different pictures over 10 seconds (64.9 unique fps). This shows that the FFmpeg timing bottleneck is fixed while another source-present/composition bottleneck remains. WinForms WM_PAINT count is not proof of DWM/IDDCX presentation; a native flip-model source is the next required comparison. See `motion-patched90/result.json` and `motion-patched90/motion.json`.

The modified FFmpeg contains no `fps=90` resampling in this test; it uses normal `ddagrab` duplicate handling for static desktops. The production FFmpeg selection was not changed by the build/test scripts.

The executable imports `CreateWaitableTimerExW` and `SetWaitableTimer` from KERNEL32. Static import inspection found only Windows system DLLs (bcrypt, GDI32, KERNEL32, msvcrt, SHELL32, USER32) and no import of `timeBeginPeriod` or `NtSetTimerResolution`.
