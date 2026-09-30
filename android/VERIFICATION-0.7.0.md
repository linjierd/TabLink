# Android 0.7.0 multi-session port verification

Artifact: `artifacts/TabLink-android-0.7.0-debug.apk` (360450 bytes)

SHA-256: `4002A86AE044CA703DCD045D7A90AB44DA948BB06FED4720F8024589589FEBA2`

Package: `com.tablink.client`, versionCode 9, versionName 0.7.0, minSdk 23, targetSdk 35. The previous checked source was 0.6.0 / versionCode 8.

Scope: PairingLink accepts precisely `27184, 27186, 27187, 27188, 27189, 27190, 27191, 27192` for native TLS sessions. Port `27185` remains reserved for the browser HTTPS endpoint and is rejected by native pairing. Parsing, private saved links and reconnection retain the selected port. Token syntax and exact certificate DER pin rules are unchanged. Decoder, fullscreen, HUD and refresh-rate implementations were not modified.

Command: `./build.ps1 -Offline` from the Android directory.

- 108 pairing, QR and pinned-TLS assertions passed. All eight native ports are parsed and round-tripped independently, with token/pin validation checked on each. Browser port 27185, neighboring ports 27183/27193, zero, negative, 65535/65536, overflowing integers, leading-zero, signed, fractional, exponent, percent-encoded and empty values are rejected. Existing real local TLS and real QR decoding checks also passed.
- 55 protocol/aspect-fit assertions, 26024 bounded render-clock assertions and 20 HUD/pause/brightness assertions passed.
- `assembleDebug` and `lintDebug` passed; 0 errors and 18 existing warnings.
- `apksigner` v1/v2 signature verification passed. `aapt dump badging` confirmed versionCode 9 / versionName 0.7.0 and unchanged INTERNET/CAMERA permissions.

No APK installation or real-device commands were performed, and no existing stream was stopped. These checks establish build and parser compatibility; concurrent physical sessions remain part of the parent task's integration validation.
