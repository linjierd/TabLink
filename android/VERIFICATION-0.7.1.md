# Android 0.7.1 candidate verification

Built 2026-09-29; application version 0.7.1, version code 10.

Candidate: `artifacts/TabLink-android-0.7.1-debug.apk`

SHA-256: `DB747316B11B43AE208903886B1FA9BEEEA5DDDE2147F54437FF1346BA33BD72`

The build step did not install or restart the tablet and did not replace the preserved 0.7.0 artifact. Subsequent repository validation installed this exact candidate and selected it for the 0.7.1 release: the final 120.418-second native-resolution dynamic test measured 89.140 actual presentations per second, with no input-queue drops in that interval. A static desktop window measured 68.897 fps; this is not a constant-90-fps guarantee. See [`docs/VERIFICATION-0.7.1.md`](../docs/VERIFICATION-0.7.1.md) for evidence and workload differences.

## Change

The network reader now waits briefly when the six-frame compressed input queue is full. The requested waiting budget is two configured frame periods, capped at 25 ms (22.22 ms at 90 fps). Java monitor waiting releases the queue lock; codec polling and the codec callback handler never wait. Polling makes room and wakes the producer. Closing or resetting the queue wakes and cancels an old waiting offer without advancing its PTS ordering state.

Timeout retains the previous complete reference-chain eviction and wait-for-IDR behavior. The queue still has six slots, the 150 ms expiry rule remains, and RenderClock, image quality, resolution and refresh selection are unchanged.

The public read-only pacing provider adds input receipt/submission counts, arrival/submission maximum gaps, separate overflow/expiry/reordered/wait-for-IDR drop counts, queue high-water mark, wait counts/timeouts/duration and input queue ages. These are bounded aggregate counters, with no per-frame logs or authentication values.

Input queue age is measured from receipt of a complete access unit by VideoDecoder to successful codec input submission. It includes the new short producer wait but excludes Windows capture, transport backlog, codec processing and Surface presentation. It is not an end-to-end latency measurement. The wait budget bounds requested monitor waiting; scheduler delays can make measured waiting longer, which the diagnostics retain.

## Build checks

- 1,957 new pure JVM queue assertions passed: normal 90 fps cadence; bursts of 7 through 12 frames; concurrent consumer progress; bounded sustained congestion; dependency-chain recovery; timeout and reason accounting; expired/duplicate/reordered inputs; clear/close wake-up; interrupted offer; duplicate PTS arriving during a producer wait.
- Existing 55 protocol, 26,024 RenderClock, 20 HUD/capture-state and 108 pairing/QR/TLS assertions passed.
- `assembleDebug` and `lintDebug` succeeded using the offline build script.
- APK v1/v2 signatures verified using the existing development signing key.

These checks establish queue/lifecycle behavior and build readiness. They do not establish improved real-device frame rate, sustained 90 fps or acceptable end-to-end latency; use the root verification records for the subsequent field comparison.
