# Native display-motion probe

Build with `./build.ps1`. It uses the already downloaded w64devkit compiler, staging an ASCII path when necessary. It changes no machine/user PATH and installs nothing.

Run the managed guard, not the native executable directly:

```
TabLink.Video.Tests.exe --d3d-motion-probe \\.\DISPLAY46 12
```

Replace DISPLAY46 only with the exact currently active TabLink VDD. `NativeMotionProbe.cs` validates the hardware-backed virtual-display identity, primary status, native bounds, and unique DXGI adapter/output before launch and throughout the run. The native helper independently requires the same native bounds, adapter LUID, non-primary status, and 90Hz display mode. Its no-activation tool window hides/exits on display/DPI/position changes and closes automatically after at most 150 seconds. The final positional argument is the requested duration in seconds (clamped to 2–150). JSON results are written to standard output; use normal process-output redirection to retain the report. This command starts only the native drawing helper, never another FFmpeg or capture process.

The source uses a D3D11 flip-discard swap chain with two buffers, maximum frame latency 1, the swap chain's frame-latency waitable object, GPU ClearView drawing, and Present(1,0). It uses no GDI painting or timer-period API. Sixteen binary cells at x=48, y=220, size=20, step=28 encode the monotonically increasing source frame ID. Each frame's Present return time is recorded using QPC, allowing a captured frame ID to be mapped back to the measured source timeline.

Present(1,0) does not guarantee that a window's source submission runs at the configured VDD rate. On the test machine, the DWM-paced source actually returned about 238 presents/s while the primary monitor was 240Hz and the VDD mode was 90Hz. This is reported as measured data; it is not called a 90fps source. A faster source is useful for verifying that a 90fps capture contains 90 genuinely different images.

The optional `third_party/ffmpeg-tablink/motion-benchmark.py --motion-kind d3d` performs a bounded capture only after another exact display/DXGI check, decodes just the local output's barcode, and measures unique frames using the source's actual Present timestamps. It never stops another application or changes display modes.
