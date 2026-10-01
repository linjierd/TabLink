# Public binary release scope

The globally downloadable GitHub binary is a preview build with a narrower
redistribution scope than a normal local source build.

TabLink is created and maintained by **张林杰 (Jey)** (GitHub
[@linjierd](https://github.com/linjierd)); the author's blog is
[Linjie / 开发笔记](https://linjie.space/).

- It includes the self-contained Windows x64 0.8.4 Preview 1 application,
  updater, signed Virtual Display Driver, two separately licensed patched
  FFmpeg helper executables plus complete corresponding source, and the
  Android 0.8.4 / versionCode 16 preview APK.
- The display lifecycle remains intentionally limited to one TabLink virtual
  display and one receiving device at a time. Encoder discovery does not add
  displays, and a second request is rejected before driver mutation.
- `tools/ffmpeg/ffmpeg.exe` is the LGPL-2.1-or-later hardware helper with
  NVENC, Intel QSV and AMD AMF support. On the release machine, two final
  1200 x 1920 @ 90 Auto probes selected NVENC and measured 125.6-127.6 fps.
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
  native Wi-Fi and USB-network modes do not need ADB. See `ADB-SETUP.md` for
  the optional USB-debugging compatibility mode.
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

The signed `stable` update channel remains at 0.8.0. Publishing 0.8.4 Preview
1 assets on GitHub must not move the stable manifest or cause installed stable
clients to update automatically.

TabLink-authored source remains MIT licensed. Every third-party component in
the binary keeps its own license and notice. `THIRD_PARTY_NOTICES.md` and the
`licenses` directory describe the exact obligations. Final 0.8.4 FFmpeg
delivery SHA-256 values are: `ffmpeg.exe`
`F47DA86A069F8F8EB30BCF42CE6137962691A9A1197386D262646A33D3D62659`,
`ffmpeg-x264.exe`
`B4C34236895D986C4ED452949515768348972DF1DC2FE8B85E81FEFEEA663EBE`,
and `source-bundle.tar.gz`
`C59D8F6D6B5FD02505D36714967183747010EE128FA29F9D967550BFCAE08D30`.
The release-level checksum file additionally covers every file in the final
Windows package; the GitHub asset checksum file covers the Windows ZIP, APK and
corresponding-source archive.
