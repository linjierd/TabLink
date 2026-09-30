# Validation on 2026-09-20

Final `bin/ffmpeg.exe`: 2,393,600 bytes; SHA-256 `AEF1CC45435077017947E4E361A3D949552774F931772BDF406F4A8F852D90EF`.

* The Windows timer branch is compiled even though `HAVE_NANOSLEEP=1` and `HAVE_USLEEP=1`; `HAVE_WINDOWS_H=1` and `HAVE_SLEEP=1` select it first.
* `timer-probe.c` linked to the actual built `libavutil/time.o`. Original Sleep median delays for requested 1/5/11.111 ms were 15.581/15.617/15.579 ms. Patched `av_usleep` medians were 1.546/5.303/11.453 ms. Handle counts remained 85 before and after 1,000 nonzero plus 1,000 zero waits. See `timer-probe-results.csv`.
* The same 64x64@90 lavfi/readrate stdout-arrival test changed median/P90 intervals from 15.167/16.009 ms to 11.075/11.349 ms. Above-14ms intervals changed from 224 to 0. The readrate test has an initial catch-up burst, so its whole-process average is not claimed as an end-to-end display rate. See `readrate-probe-results.json`.
* Production `H264Encoder` and `AnnexBParser` delivered all 450 synthetic 1200x1920@90 frames in 3.022 seconds (148.89 frames/s throughput). All parser/DXGI identity tests, concurrent repeated disposal, and abrupt-owner-exit JobObject cleanup passed: 11 tests. See `production-parser-test.log`.
* Independent FFprobe inspection: H.264 High, level 5.1, 1200x1920, no B frames, exactly 450 decoded frames. NVENC initialized using the existing RTX 4060 Laptop GPU driver; no driver installation or update occurred.
* A bounded selected-DISPLAY46 DDA90 motion test emitted 895 frames in 10 seconds with 9.59% of one CPU core. The WinForms source painted 90.06 unique frame numbers per second, but the encoded barcode showed only 650 different pictures over 10 seconds (64.9 unique fps). This shows that the FFmpeg timing bottleneck is fixed while another source-present/composition bottleneck remains. WinForms WM_PAINT count is not proof of DWM/IDDCX presentation; a native flip-model source is the next required comparison. See `motion-patched90/result.json` and `motion-patched90/motion.json`.

The modified FFmpeg contains no `fps=90` resampling in this test; it uses normal `ddagrab` duplicate handling for static desktops. The production FFmpeg selection was not changed by the build/test scripts.

The executable imports `CreateWaitableTimerExW` and `SetWaitableTimer` from KERNEL32. Static import inspection found only Windows system DLLs (bcrypt, GDI32, KERNEL32, msvcrt, SHELL32, USER32) and no import of `timeBeginPeriod` or `NtSetTimerResolution`.
