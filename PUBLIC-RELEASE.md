# Public binary release scope

The globally downloadable GitHub binary is a preview build with a narrower
redistribution scope than a normal local source build.

TabLink is created and maintained by **张林杰 (Jey)** (GitHub
[@linjierd](https://github.com/linjierd)); the author's blog is
[Linjie / 开发笔记](https://linjie.space/).

- It includes the self-contained Windows x64 0.8.7 Preview 1 application,
  updater, signed Virtual Display Driver, two separately licensed patched
  FFmpeg helper executables plus complete corresponding source, and the
  Android 0.8.7 / versionCode 19 preview APK.
- The optional ADB USB compatibility path assigns a cryptographically random
  device-side port to each session, while forwarding to the fixed loopback
  frame server on the PC. Its protected ProgramData receipt is first persisted
  as non-removal `Prepared` state and becomes cleanup-authorizing `Owned` state
  only after the exact `--no-rebind` command reports success. Receipts bind the
  originating Windows user SID. A cross-user `Prepared` receipt can be sealed
  without ADB because it never held removal authority; a cross-user `Owned`
  receipt is retained and never reaches ADB under the current user. Deferred
  cleanup is fairly rotated by a persistent pending-queue sequence rather than
  filesystem timestamps, legacy LocalAppData records cannot authorize deletion,
  and display reclamation never waits for USB cleanup. An eligible cleanup reads
  the exact reverse mapping again immediately before removal. ADB still has no
  cross-process atomic compare-and-delete operation, so a local administrator
  can race the narrow interval after that final check. TabLink never issues
  `kill-server`, `--remove-all`, `tcpip`, or a command against an implicit/default
  device.
- The 0.8.5 media clock follows real monotonic elapsed time when capture is
  slower than the requested rate. Its patched DDA helper uses a nonblocking
  desktop query only after a valid cached frame exists with `dup_frames=1`;
  initial acquisition, probing and recovery retain a bounded wait. On the
  verified W202DS chain, ordinary-desktop Android receive/render callbacks
  rose from about 65 fps to 89.98/89.95 fps with zero reported drops.
- The display lifecycle remains intentionally limited to one TabLink virtual
  display and one receiving device at a time. Encoder discovery does not add
  displays, and a second request is rejected before driver mutation. Merely
  launching TabLink, opening pairing, or waiting for a scan does not keep an
  active virtual display. TabLink checks and creates its single display only
  after an authenticated receiver supplies a valid display profile; normal
  disconnect, preparation failure, timeout, or owner exit removes the exact
  active device owned by that session. The signed driver package and one-output
  mode configuration remain staged for a later verified connection.
- The current renewable display lease, ownership marker and immutable bootstrap
  live below protected ProgramData `TabLink\DisplayLeases`, keyed globally by
  the physical VDD target and serialized by one machine-wide protected file
  lock across Windows users and interactive sessions. LocalAppData retains only
  per-user `.last.json` / `last-display.json` layout preferences; those files
  cannot authorize the elevated watcher to reclaim a display.
- `tools/ffmpeg/ffmpeg.exe` is the LGPL-2.1-or-later hardware helper with
  NVENC, Intel QSV and AMD AMF support. On the release machine, the final
  1200 x 1920 @ 90 synthetic NVENC/parser run delivered 450/450 frames at
  170.17 fps encoder throughput. A bounded dynamic-desktop barcode capture
  encoded 899 frames with 898 different source IDs, or 89.746 different
  source pictures/s by the source's measured QPC timeline.
  Forced QSV failed closed because this host has no supported MFX
  implementation (`-9`), and forced AMF failed closed because this non-AMD
  host has no `amfrt64.dll`; neither attempt changed backend. Validation on
  compatible Intel and AMD machines remains pending and must not be
  represented as passed.
- `tools/ffmpeg/ffmpeg-x264.exe` is a separate GPL-2.0-or-later helper using
  libx264. TabLink uses it only after explicit user authorization and caps its
  effective video target at 30 fps. The software cap does not change the
  virtual display resolution or refresh mode.
- It does **not** include Google Android SDK Platform-Tools binaries. Android
  native Wi-Fi and USB-network modes do not need ADB. The optional USB-debugging
  compatibility mode accepts only the official Windows Platform-Tools r37.0.0
  `adb.exe`, `AdbWinApi.dll` and `AdbWinUsbApi.dll` fixed-hash triplet. On first
  interactive use TabLink verifies and copies that triplet into protected
  ProgramData before executing it. A successfully staged protected copy can be
  re-verified and reused by later background cleanup even though the public ZIP
  does not ship Google binaries; cleanup never falls back to a user path,
  environment variable or `PATH`. See `ADB-SETUP.md`.
- It does **not** include the SIPSorcery-based browser WebRTC receiver because
  SIPSorcery 10.0.16 has an additional geographic distribution restriction.
  The browser page explains this at runtime. Android native receiving remains
  available.
- TabLink's Windows executables are not Authenticode code-signed. Windows will
  therefore identify the publisher as unknown when requesting the required
  administrator permission. The bundled third-party UMDF virtual-display
  package keeps its upstream INF, catalog and DLL unchanged. The catalog and
  DLL pass the release machine's generic Authenticode `/pa` policy, and the
  catalog covers the exact INF and DLL. This is not a Microsoft WHQL or
  attestation signature; the release machine's `/kp` kernel-policy check did
  not accept the SignPath/GlobalSign chain. Installation remains subject to
  each Windows machine's driver trust policy. TabLink does not install
  certificates, enable test signing, disable Secure Boot or weaken that policy.
- The APK is a non-debuggable release build signed with the project's existing
  development key so earlier TabLink test installations can upgrade in place.
  It is for this preview and is not an app-store production signature.

The Windows public ZIP is self-contained for Windows x64 and does not require
a separate .NET installation.

The signed `stable` update channel remains at 0.8.0. Publishing 0.8.7 Preview
1 assets on GitHub must not move the stable manifest or cause installed stable
clients to update automatically.

TabLink-authored source remains MIT licensed. Every third-party component in
the binary keeps its own license and notice. `THIRD_PARTY_NOTICES.md` and the
`licenses` directory describe the exact obligations. Final 0.8.5 FFmpeg
delivery SHA-256 values are: `ffmpeg.exe`
`BB1FA5F2A5CC572C6A1D310F88348324EE43B84DF5A778FD0AF02D77B3C86627`,
`ffmpeg-x264.exe`
`6E3EA733AD40DA6D6D78C2DFC51BCCA950C3519D3304AE045316F7D55B89EDB7`,
and `source-bundle.tar.gz`
`FD7977F53EDD262D55C49F200EB5F54B1B12F5FFA547770380448708D75EA6F2`.
The release-level checksum file additionally covers every file in the final
Windows package; the GitHub asset checksum file covers the Windows ZIP, APK and
corresponding-source archive.
