# Public binary release scope

The globally downloadable GitHub binary is a preview build with a narrower
redistribution scope than a normal local source build.

- It includes the self-contained Windows x64 0.8.1 application, updater,
  signed Virtual Display Driver, patched FFmpeg executable plus complete
  corresponding source, and the Android 0.8.1 preview APK.
- It does **not** include Google Android SDK Platform-Tools binaries. Android
  native Wi-Fi and USB-network modes do not need ADB. See `ADB-SETUP.md` for
  the optional USB-debugging compatibility mode.
- It does **not** include the SIPSorcery-based browser WebRTC receiver because
  SIPSorcery 10.0.16 has an additional geographic distribution restriction.
  The browser page explains this at runtime. Android native receiving remains
  available.
- TabLink's Windows executables are not Authenticode code-signed. Windows will
  therefore identify the publisher as unknown when requesting the required
  administrator permission. The bundled virtual display driver is separately
  signed and verified before installation.
- The APK is a non-debuggable release build signed with the project's existing
  development key so earlier TabLink test installations can upgrade in place.
  It is for this preview and is not an app-store production signature.

The Windows public ZIP is self-contained for Windows x64 and does not require
a separate .NET installation.

TabLink-authored source remains MIT licensed. Every third-party component in
the binary keeps its own license and notice. `THIRD_PARTY_NOTICES.md` and the
`licenses` directory describe the exact obligations.
