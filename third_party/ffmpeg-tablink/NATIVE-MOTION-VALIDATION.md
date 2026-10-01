# Native-motion validation

## 0.8.5 end-to-end dynamic source check — 2026-10-01

The 0.8.5 candidate was exercised with the native D3D11 flip-discard source on the one verified active TabLink VDD. The source process exited successfully after 40 seconds and reported 9,474 Present calls, or 236.850402 presents/s, with 4.1695 ms median and 4.3528 ms P90 Present gaps. That source rate is well above the 90 Hz capture target and rules out an under-supplied test window.

During the same run, the W202DS Android receiver measured 2,833 received/submitted frames over 31.4629 seconds (90.04246 fps) and 2,832 decoder outputs/render callbacks (90.01068 fps). Input, overflow, expired, keyframe-wait, late, crowded, out-of-order and backpressure drop counters were all zero. SurfaceFlinger recorded 2,658 new actual presents over 30.2105 seconds (87.98259 fps), P95 11.133 ms, P99 22.207 ms and a 33.331 ms maximum, with no sampling coverage gap.

This run proves the patched helper maintains the approximately 90 fps transport and decode cadence while the source is changing continuously. It does not itself decode and count every 16-bit barcode in the H.264 stream, so it is not used to claim that all 2,833 access units contained distinct source IDs.

The final `7.0.2-tablink-085-hardware2` helper was then tested with the bounded barcode method against the same uniquely verified `\\.\DISPLAY19`. The current VDD bounds were 1536×960 at 90 Hz. In 10 seconds it encoded 899 pictures containing 898 different source IDs; there was one repeated ID, 897 transitions, and **89.7460487 different source pictures/s** when measured against the source's own QPC Present timeline. The source ran at 239.291627 presents/s, the capture used 7.13% of one CPU core, and no `fps` resampling filter was present. The sanitized evidence is `motion-085-final-result.json`; the local full run remains under `artifacts/device-verification/motion-085-final/`, and the bounded process exit code was 0.

## Final native-motion comparison — 2026-09-20

The earlier WinForms result measured a GDI source/composition limit. A native D3D11 flip-discard source removes that limitation and establishes the modified FFmpeg's real capture improvement.

Both tests used the same verified TabLink VDD: `\\.\DISPLAY46`, adapter LUID 0:79247, DXGI adapter 0/output 1, native 1200x1920 at x=2560/y=0. The primary screen remained separate. The source drew a moving bar and 16-bit monotonically increasing frame-ID barcode directly with D3D11.1 ClearView. Frame latency was 1 and every Present used sync interval 1. Each actual Present return was timestamped with QPC.

| FFmpeg | Encoded pictures / 10s | Different source pictures | Repeated source IDs | Unique fps from measured source timeline | One-core CPU |
|---|---:|---:|---:|---:|---:|
| Original Gyan 7.0.2 | 691 | 691 | 0 | 68.958 | 9.38% |
| TabLink 7.0.2 private high-resolution timer | 898 | 898 | 0 | 89.700 | 9.29% |

No `fps` filter, duplicated-frame inflation, driver update, global timer setting, or registry edit was used. Both commands used ordinary `ddagrab framerate=90,dup_frames=1` and the same NVENC parameters. The separate production TabLink session continued running throughout the tests.

The source itself presented at 237.62–237.93fps, paced by DWM alongside the 240Hz primary monitor. The virtual display's configured mode remained 90Hz. Because source presentation was faster than capture, captured IDs normally advanced by two or three frames; every modified-encoder output contained a genuinely new source image. Counting WM_PAINT or nominal timestamps was not used as proof.

Evidence:

* `motion-native-d3d90/result.json`, `motion.json`, `command.json`, `capture.h264`, `ffmpeg.log`.
* `motion-native-d3d90-original/result.json`, `motion.json`, `command.json`, `capture.h264`, `ffmpeg.log`.
* Native helper source and build instructions: `../../tests/TabLink.Video.Tests/native/`.

When the 2026-09-20 comparison was recorded, the encoder binary and then-published `source-bundle.tar.gz` were unchanged. On 2026-10-01 the 0.8.5 executable was rebuilt with both timing patches and the neutral `085` compile-time prefix; the current-helper barcode result is recorded above, while Android presentation and USB end-to-end measurements remain separate host/client health checks. See `VALIDATION.md` and `SHA256SUMS` for final identities.
