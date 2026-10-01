# Final native-motion comparison — 2026-09-20

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

When this native-motion report was recorded, the encoder binary and then-published `source-bundle.tar.gz` were unchanged. On 2026-10-01 the executable was rebuilt from the same patched FFmpeg and codec-header sources with a neutral compile-time prefix, and the corresponding source bundle was refreshed; see `VALIDATION.md` and `SHA256SUMS` for the current hashes. The historical native-motion measurements were not rerun, while the current executable separately passed the production synthetic NVENC/parser test. Android presentation and USB end-to-end measurements are separate host/client health checks managed by the main task.
