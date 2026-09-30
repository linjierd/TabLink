# Android 0.8.0 自动更新验证

验证日期：2026-09-29。候选包为 `artifacts/TabLink-android-0.8.0-debug.apk`，包名 `com.tablink.client`，versionName `0.8.0`，versionCode `11`，minSdk 23，targetSdk 35。

构建通过 `build.ps1 -Offline -UpdateManifestUrl 'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json'` 完成。生成的 `BuildConfig` 已核对为该 HTTPS 地址及固定 P-256 公钥。`aapt dump badging` 确认版本和权限；新增权限是 `android.permission.REQUEST_INSTALL_PACKAGES`。

纯逻辑与构建结果：

- 协议与坐标：55 项通过。
- 有界视频输入队列：1,957 项通过。
- 有界渲染时钟：26,024 项通过。
- HUD 与采集暂停策略：20 项通过。
- 配对、二维码与 TLS 固定：108 项通过。
- 正式版更新签名、安全策略及状态机：44 项通过；包括正式 DownloadSite 查询地址、URL 凭据和片段拒绝、整秒 UTC 规则及跨平台 cohort 固定向量。
- `assembleDebug`、`lintDebug` 与 APK 签名验证通过。
- Windows 和 Android 使用同一个由正式离线私钥签名的 envelope fixture；原样验签通过，payload 或 signature 被修改后拒绝。

APK 大小为 394,442 字节，SHA-256 为 `DD7B26A4E3EB2AE8C69574528C0B5A822BBEFAC6C550C884E358FC8DA9F4B12D`。签名证书 SHA-256 为 `B0035FFE0539E43DED2F5C40E3B7E4D4EDFB5D8F8063459FACA911EDC7500554`；v1 / v2 APK 签名通过。该证书延续当前平板已安装应用的开发签名，用于原地升级，不是商店正式签名。

本地静态验证覆盖：严格 stable 清单、P-256 验签、下载大小与 SHA-256、清单撤回、分批发布、二次安装校验、投屏期间延期，以及 `PackageInstaller` 成功、失败、取消和需要系统确认的状态转移。Android 是否准许无确认安装仍由设备、安装来源和系统策略决定。

实体 W202DS 的安装、未知来源授权、`PackageInstaller` 回调、线上清单下载以及升级后原生副屏重连证据由根目录 `VERIFICATION-0.8.0.md` 记录；在这些步骤完成前，本文件不把构建通过当作真机自动更新成功。
