# Third-party components in TabLink Android 0.7.0

## ZXing core 3.5.3

- Project: [ZXing official repository](https://github.com/zxing/zxing)
- Component: `com.google.zxing:core:3.5.3` (QR image decoding only; not the old external Barcode Scanner application)
- License: Apache License 2.0
- Pinned source version: [zxing-3.5.3](https://github.com/zxing/zxing/tree/zxing-3.5.3)
- Binary source: [Maven Central core-3.5.3.jar](https://repo.maven.apache.org/maven2/com/google/zxing/core/3.5.3/core-3.5.3.jar)
- Local build cache: `.tools/dependencies/zxing-core-3.5.3.jar` (downloaded from the fixed Maven Central URL and SHA-256 checked; not committed)
- SHA-256: `8d8064c1636fdaef7189dd9055c7d59950a8940a12f2293956446ec3c109fd82`
- Upstream SHA-1 was checked against the adjacent Maven Central `.jar.sha1` file when downloading. Every local build additionally verifies the fixed SHA-256 above.

The unmodified upstream [LICENSE](https://raw.githubusercontent.com/zxing/zxing/zxing-3.5.3/LICENSE) and [NOTICE](https://raw.githubusercontent.com/zxing/zxing/zxing-3.5.3/NOTICE) are included in `app/src/main/assets/licenses/` and therefore in the APK.

Only ZXing core is embedded. Native Android camera access, permission handling, pairing UI and TLS connections are implemented by TabLink. No Google Play services, analytics, external scanning application or remote image decoding service is used.
