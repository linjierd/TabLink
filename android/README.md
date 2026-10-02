# TabLink Android 客户端 0.8.9 Preview 1 源码候选

<!-- tablink-version-contract: version=0.8.9; channel=preview; preview=1; androidVersionCode=21 -->

作者：**张林杰（Jey / [@linjierd](https://github.com/linjierd)）** · 博客：[Linjie / 开发笔记](https://linjie.space/)

供 Windows TabLink 通过 Wi-Fi、USB 网络共享或既有 ADB 通道连接的安卓平板客户端。包名 `com.tablink.client`，启动组件 `com.tablink.client/.MainActivity`。支持 Android 6.0（API 23）及以上；当前构建目标为 Android 15（API 35）。

Wi-Fi 与 USB 网络共享使用相同的 TLS 配对通道，无需 ADB 或 USB 调试。既有 ADB 通道仍仅通过 `127.0.0.1` 明文连接，由电脑端对用户选定设备建立 `adb reverse`。APK 不枚举或切换 USB 设备模式，不使用 Android Open Accessory 模式；“打开 USB 网络共享设置”只打开系统设置页面，由用户操作共享开关。

当前优先使用 H.264 硬件解码，并兼容旧版 JPEG 画面。启动即进入沉浸全屏，没有常驻控制条或全屏按钮。支持原比例显示、当前方向与屏幕模式上报、保持屏幕常亮、重连、退出，以及单指鼠标点击和拖动。当前没有双指滚动、音频、压感笔或键盘输入；电脑端负责视频编码，安卓端负责解码。

真正的扩展桌面由 Windows 虚拟显示驱动提供；本 APK 负责显示电脑端选定的桌面画面，本身不能创建 Windows 显示器。

## 无需 USB 调试的连接

尚未登记电脑时，应用显示“扫描电脑二维码”“粘贴连接链接”“打开 USB 网络共享设置”。Wi-Fi 连接时，平板和电脑应处于同一局域网；USB 连接时，接好数据线并在系统设置开启 USB 网络共享，然后扫描电脑端显示的对应 IPv4 地址二维码。没有相机、拒绝相机权限或相机被占用时，仍可粘贴链接。登记成功后，应用启动会先尝试保存的地址；地址不可用时再发现同网段内具有相同电脑身份的端点。电脑端已有可信设备时会自动启动可信监听，但不会因此发布二维码；需要登记新设备时，用户必须显式点击“生成新配对二维码”。

二维码是有效期五分钟且只能成功使用一次的登记凭证，不包含显示器尺寸或刷新率：

```text
tablink://connect?host=<IPv4>&port=27184&token=<64 lowercase hex>&cert=<64 hex SHA256 DER>
```

客户端拒绝未知或重复参数、额外路径和片段、DNS 名称、IPv6、非规范 IPv4、环回/多播地址、错误端口，以及不符合长度或字符要求的 token/证书指纹。0.8.0 支持八个原生会话端口：`27184`、`27186`、`27187`、`27188`、`27189`、`27190`、`27191`、`27192`；`27185` 专门保留给浏览器 HTTPS，原生客户端拒绝此端口。首次登记完整保留二维码端口；可信发现成功后保存实际响应端口，不会静默改回默认端口。其他端口及带前导零、正负号、百分号编码等非规范形式均拒绝。证书指纹允许十六进制大小写，token 只允许小写。

网络连接只启用 TLS 1.2/1.3。`PinnedTls.CertificatePin` 使用常量时间比较验证服务端叶证书完整 DER 的 SHA-256 必须等于登记时保存的 `cert`，同时检查证书有效期。0.8.8 及以后的电脑证书跨会话保持，证书指纹同时作为 `hostId`；不能用系统可信 CA 的另一张证书替代，也没有跳过验证的回退路径。证书校验失败时不会发送认证包，已登记设备也不会因局域网发现结果而跳过固定证书。

首次 TLS 握手成功后，客户端创建或读取 Android Keystore 中不可导出的 P-256 私钥，在 `0x10` 中声明 `trusted-device-v1`，并随一次性 token 提交设备公钥、由公钥 SHA-256 派生的 `deviceId` 和设备名称。电脑返回 `0x1a` 前已消费该 token；同一码再次提交以及生成后满五分钟的 token 都会被拒绝。用户点击“生成新配对二维码”会轮换 token，新码生成后旧码立即失效，新码也仍只能成功使用一次。后续 TLS 连接以 `0x17` 声明设备身份，接收 `0x18` 的全新 32 字节挑战，用 Keystore 私钥生成 SHA-256 ECDSA DER 签名并通过 `0x19` 返回。签名内容绑定协议域、`hostId`、`deviceId` 和本次挑战；旧签名不能重放到新连接。二维码过期或轮换不影响已经登记设备的签名重连。

认证后客户端同步发送 `0x13` 当前原生显示能力和请求刷新率，然后才读取和解码视频。`render-submitted-v1`、`decoder-refresh-v1`、`receiver-feedback-v1` 与 `adaptive-video-v1` 的既有协商不变；只有电脑端在 `0x02` 状态中回显相应能力后，客户端才会发送对应的 `0x14` 解码提交、`0x15` 关键帧恢复请求或 `0x16` 独立接收端反馈。旋转、电脑监听重建、IPv4 变化或电脑端选择新的 Wi-Fi / USB 网络共享线路后都可重新认证，不要求再次扫描二维码；如果旧线路仍保持 Up，电脑端不会自行猜测切换到新出现的线路。

应用私有 `SharedPreferences("trustedComputer", MODE_PRIVATE)` 只保存电脑身份、固定证书指纹、最后 IPv4 和端口等公开信任元数据；私钥留在 Android Keystore，bearer token 不持久化。自 0.8.8 起，启动时会删除旧版 `SharedPreferences("pairing")` 中的 `lastLink` bearer 记录。用户点击“忘记上次配对的电脑”时，会同时删除公开信任元数据与对应 Keystore 密钥；电脑端撤销设备后，该设备的所有后续签名都会被拒绝，需要新二维码重新登记。

局域网发现使用 UDP 27193。请求指定已保存的 `hostId` 和随机 nonce，电脑只在同子网、限速校验通过时返回相同 nonce、当前端口和 host ID。Android 在完整发现期限内收集有界候选，最多保留八个且每个来源 IP 只保留一个，不把第一个响应当作可信电脑；随后逐个候选连接，并在同一套接字上先精确核对持久证书，再完成新挑战签名。发现不传 token、证书、公钥列表或私钥，也不改变信任。路由器客户端隔离、VPN/TUN 或不同子网可能阻止发现，此时可在目标线路生成新二维码登记。

扫码由独立、未导出的 `QrScannerActivity` 使用原生相机和内嵌 ZXing core 完成；不依赖 Google Play、第三方扫码应用或网络识别服务。相机只在用户打开扫码页面后申请权限，图像仅在内存中处理，离开页面后释放相机。ZXing 的来源、校验值和 Apache-2.0 许可见 `THIRD_PARTY_NOTICES.md`，许可文本也打包在 APK 的 `assets/licenses` 中。

## 全屏观看与统计设置

观看时只保留统计文字。默认白色、透明度 30%，即不透明度 70%，背景完全透明。**长按统计文字，或使用 Android 返回手势 / 返回键**，即可打开设置；系统导航栏可通过边缘滑动临时显示。

设置内可选择九宫格位置、五种常用文字颜色或输入自定义 `#RRGGBB`，并调整 0–100% 透明度。设置明确同时显示透明度和不透明度，立即生效并保存到应用偏好；下次打开仍保留。100% 透明会隐藏文字，此时使用返回手势进入设置。

设置面板中的“完成”关闭面板继续观看，“重连”重新建立当前网络或 ADB 会话，“退出”停止连接并关闭应用。单纯打开或调整显示设置不停止视频、不重建解码器；点击“更换连接”才回到连接面板。原生分辨率、刷新率请求和 0.4.2 已验证的呈现节拍算法保持不变。

## 正式版自动更新

Android 客户端在进入前台时立即读取与电脑端相同的双源签名 stable 清单，应用保持打开期间每 6 小时复查。设置面板提供“自动更新”“自动下载后手动安装”“从不更新”三种策略，首次运行默认自动更新；损坏的偏好失败关闭为从不更新。投屏期间只检查，不下载或安装；停止投屏后继续。自动更新会在空闲下载、重新确认当前最新签名决定，然后直接交给 Android 系统安装器；没有额外的应用内确认，但 Android 系统确认仍然保留。自动下载后手动安装必须先由用户在应用内明确选择安装。

发布构建必须同时通过 `-UpdateManifestUrl` 和 `-UpdateManifestFallbackUrl` 注入两个 HTTPS 清单地址。客户端独立验证每个来源的内置 P-256 公钥签名，选择最新权威决定，再核对 stable SemVer、versionCode、包名、APK 签名、大小和 SHA-256。同一发布时间出现两个有效签名但语义冲突的决定时，客户端会持久阻断该时间及更早的清单，直到收到发布时间更晚的有效签名决定；解除阻断与清除旧安装事务使用同一次持久化提交。安装只使用 Android `PackageInstaller`：每次提交都有独立随机尝试令牌，回调的系统 sessionId、私有 sessionId 和令牌必须同时匹配当前事务；进程重启时只恢复仍存在于 `PackageInstaller.getMySessions()` 且已 sealed 的 session，无法可靠读取 sealed 状态的 Android 6–7.1 会清理旧 session 并进入重试。Android 12 及以上会请求无需用户操作，但系统仍可要求显示标准确认页。安装会话失败时保留已验证的暂存 APK 供重试，不再通过 `ACTION_VIEW` 或应用 FileProvider 打开无法可靠跟踪的兼容安装界面。当前直接分发包继续使用既有开发签名，以便已安装的平板原地更新；切换到新的商店正式签名前必须单独安排签名迁移。

## 构建

要求 JDK 17 或 21、Gradle 8.13、Android SDK platform 35 和完整的 build-tools 35.0.0。Android Gradle Plugin 固定为 8.13.2。二维码依赖固定为 Maven Central 的 ZXing core 3.5.3；首次构建需要下载依赖，缓存完整后才可使用 `-Offline`。

在本机项目目录中，以下命令会运行协议与坐标测试、编译 APK、执行 Android Lint、校验 APK 签名：

```powershell
Set-Location '<repository-root>\android'
.\build.ps1 `
  -UpdateManifestUrl 'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json' `
  -UpdateManifestFallbackUrl 'https://github.com/linjierd/TabLink/releases/latest/download/manifest.json'
```

本机构建采用的精确工具参数：

```powershell
.\build.ps1 `
  -JavaHome '<path-to-jdk-21>' `
  -AndroidSdk '<path-to-android-sdk>' `
  -Gradle '<path-to-gradle-8.13>\bin\gradle.bat' `
  -UpdateManifestUrl 'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json' `
  -UpdateManifestFallbackUrl 'https://github.com/linjierd/TabLink/releases/latest/download/manifest.json' `
  -Offline # 仅在 Gradle 与 Maven 依赖已经完整缓存时使用
```

其他电脑可通过三个参数指定工具路径；首次需要下载 Gradle 插件依赖时省略 `-Offline`。脚本只在当前进程设置 Java/SDK 环境变量，并在退出时恢复。

普通构建输出 `artifacts/TabLink-android-0.8.9-debug.apk`，并可在缺少开发密钥时创建被 Git 忽略的 `build/signing/debug.keystore`。增加 `-ReleasePreview` 会运行 `assembleRelease` / `lintRelease`，输出不可调试的 `artifacts/TabLink-android-0.8.9-preview.apk`；此模式绝不临时生成新的发布预览身份，而是要求上述密钥已经存在，且 `apksigner --print-certs` 得到的 SHA-256 必须精确匹配 `b0035ffe0539e43ded2f5c40e3b7e4d4edfb5d8f8063459faca911edc7500554`，缺失或不匹配即失败关闭。脚本还通过 `aapt` 强制核对包名 `com.tablink.client`、`versionName 0.8.9` 和 `versionCode 21`（build 21）。该固定身份用于覆盖早期 TabLink 测试安装，并不是应用商店生产签名；应安全保留同一份私钥，绝不能提交到仓库。根目录公共构建同时拒绝 `-PublicRelease -SkipAndroid`，因此公开 Windows 包不能绕过对应 Android 预览 APK 的构建与身份门禁。

本机构建时发现系统 SDK 的 build-tools 35.0.0 目录只有未完成安装记录，因此在项目 `.tools/sdk` 中准备了隔离 SDK：复制现有 SDK platform 35，并从 [Google Android 官方仓库](https://dl.google.com/android/repository/build-tools_r35_windows.zip) 下载 build-tools 35.0.0。压缩包使用 [官方 repository 元数据](https://dl.google.com/android/repository/repository2-1.xml) 中 SHA-1 `af059bb67cf7786f45ee0db85e2d24985df1b4b6` 校验。没有修改系统 SDK。`.tools`、`.gradle`、`build` 和 `app/build` 属于本地构建工具或缓存，不应放进用户发行包。

## 电脑端调用

电脑端应先检查用户选定的设备未被排除，并且是获准使用的 USB 调试设备。以下命令中的 `SERIAL` 必须是该设备的真实序列号，`USER_ID` 必须是同一设备通过 `am get-current-user` 回读的当前前台用户；不要批量对所有设备执行。

```text
adb -s SERIAL install --user USER_ID --no-streaming -r TabLink-android-0.8.9-preview.apk
adb -s SERIAL reverse --no-rebind tcp:RANDOM_DEVICE_PORT tcp:27183
adb -s SERIAL shell am start --user USER_ID -n com.tablink.client/.MainActivity --es host 127.0.0.1 --ei port RANDOM_DEVICE_PORT --es token RANDOM_SESSION_TOKEN
```

以上命令只用于原有 ADB 兼容通道。`token` 是每次会话由电脑端生成的随机凭证；不要使用示例常量或将凭证记录到日志。`host` 可省略，其他主机地址会被拒绝；ADB 端口范围为 1024–65535，默认 27183。应用使用 `singleTask`，电脑端再次启动可替换旧会话。网络连接使用上述配对 URI 的 `ACTION_VIEW`，不会把非环回地址传入明文 ADB 通道。

本机中兴安装器可能使 ADB 安装停在等待确认。此时应从平板正常的文件管理器或已获准安装应用的浏览器打开对应 APK，按标准更新界面完成安装，再核对实际 versionCode；旧的“安装完成”页面不能证明新版本已安装。不需要关闭安装验证器或更改全局安全设置。

应用进入后台、用户点击退出或新会话取代旧会话时会关闭连接、结束工作线程并释放位图。恢复前台时使用当前启动参数连接。正常断线后按 1、2、4、8、10 秒间隔自动重试；成功收到画面后重置退避。收到电脑端错误包后停止自动重试，由用户或电脑端重新发起连接。

## 0.8.5 AVC 编码器兼容

电脑端 0.8.5 可以从 NVIDIA NVENC、Intel QSV、AMD AMF 和显式授权的 libx264 中选择 H.264 后端。Android 端不依赖具体厂商，仍只接受协议 v1 的 `codec:"video/avc"` 配置，并让 MediaCodec 按现有硬件优先策略选择本机 decoder。`0x20` 可附带 `encoder` 等诊断字段；它们是可选字段，旧客户端和本客户端都不以这些字段决定解码安全边界。

AVC 配置要求正数宽高和帧率、有效的 Base64 SPS/PPS，并拒绝超过 16,000,000 像素的画面。未知的未来 JSON 字段继续忽略，以保持协议 v1 向前兼容。首个访问单元以及配置变化后的首个访问单元必须是带 SPS/PPS 的 IDR；不同 Windows 后端都禁用 B 帧并统一 AUD 边界。违反这些条件会结束当前异常视频配置，不会把损坏参数交给 MediaCodec。

软件 x264 由电脑端单独授权和限速，Android 只会看到相应配置中的实际有效 fps。x264 最高 30 fps 并不表示平板面板或 Windows 虚拟显示模式被改成 30 Hz；原生方向、分辨率、请求刷新率与面板当前 Hz 仍分别上报和显示。

## 屏幕能力与刷新率

只读接口 `content://com.tablink.client.display/capabilities` 返回单行 `json` 列。Provider 需要系统级 `android.permission.DUMP`，设计供持有该权限的系统组件或 ADB shell 诊断使用，普通第三方应用不能轮询这些运行指标；目标设备的 ADB shell 是否获授该权限仍须实机确认。接口不提供文件、连接 token 或设备控制功能。

```text
adb -s SERIAL shell content query --uri content://com.tablink.client.display/capabilities
```

数据包括当前方向的 `width` / `height`、Android `rotation`（0–3）、原生模式尺寸、`activeModeId` / `refreshRate`、`supportedModes`，以及最高原生分辨率模式的 `preferredModeId` / `maxRefreshRate`。`requestedModeId` / `requestedRefreshRate` 是应用请求值，不能当作已生效值。方向或显示模式变化通过 `0x13` 上报。

若 OEM 阻止尚未启动的应用在后台提供查询，可先运行下列入口再查询。它清空 token、申请当前窗口的首选刷新率并等待电脑连接，不连接旧会话，也不启用最低亮度补偿。

```text
adb -s SERIAL shell am start -n com.tablink.client/.MainActivity --ez profileOnly true
```

刷新率请求仅作用于应用窗口和视频 Surface，不写系统全局刷新率。Android 12 及以上使用 `FIXED_SOURCE` 和 `CHANGE_FRAME_RATE_ALWAYS`，允许系统切换显示模式；系统仍可能因亮度、功耗或厂商策略拒绝请求。

**0.4.1 的低亮度处理：** 本机实测 `SCREEN_BRIGHTNESS=1`，系统资源最大值为 255、最小值为 1，同时有低于 16 时固定 60 Hz 的防闪烁策略。只有正在显示新画面、原生模式支持超过 60 Hz、实际刷新率低于请求值、系统亮度处于 0–16 且运行时再次确认最大刻度为 255 时，应用才将自己的窗口亮度临时设为 `0.08`。较高的系统或已有窗口亮度不会被降低；未知刻度（例如 4095）不启用补偿。达到目标刷新率后会保留该临时值直到流结束，以免反复切换；停止、断线或退出恢复原窗口亮度。此功能没有 `WRITE_SETTINGS` 权限，也不写全局亮度。生效时显示“高刷最低亮度”，能力接口中 `brightnessWorkaroundActive=true`。

角落 HUD 将“面板 Hz”“请求 Hz”“解码提交 fps”和“呈现回调 fps”分开显示。请求值只说明应用提出了模式请求，提交值只说明压缩帧进入 MediaCodec，呈现回调也不等于物理屏幕最终可见帧率；缺失或超过 5 秒未更新的数值显示 `—`，不会拿目标 90 Hz 补成实际 FPS。需要精确验收时仍须读取 SurfaceFlinger 的实际呈现时间。

本机 W202DS 的普通“屏幕刷新率”已经选择 90 Hz，但中兴策略仍会让部分应用使用 60 Hz。经平板可见的开发者选项“锁定刷新率”开启后，0.4.1 已实际运行在原生 1200 × 1920、90 Hz 模式；该开关锁定的是当前已选择模式的最高刷新率。这是本次验收通过系统设置界面完成的显示性能设置，APK 不会自行修改。原值已保存在发行目录 `diagnostics/android-display-settings-before.json`。此时全局亮度仍为 1，`brightnessWorkaroundActive=false`，无需应用亮度补偿。

## 0.4.2 呈现节拍对照

`renderPacing` 默认开启；可显式关闭以对照解码完成立即交给 Surface 的行为。开关只在当前应用会话的内存中生效：将媒体 PTS 映射到安卓单调时钟，目标提前量为两帧、最多 25 ms（90 fps 时约 22.22 ms）。这以约 22 ms 的额外计划等待换取更均匀的呈现节拍。输出时间过早会收紧时钟；迟到且已有更新输入的旧解码输出会放弃显示，不破坏压缩帧的参考链。长停顿、配置变化、重新连接或切换开关会重新建立时间基准，较慢输入不会一直丢帧。这个限制仅约束新增的输出排程等待，不等于整个 USB 链路的延迟上限，也不保证所有设备或负载始终呈现满 90 fps。

媒体时间戳与实际到达时钟会缓慢偏移。连续两帧的计划提前量低于目标的 75%（90 fps 时约 16.67 ms）时，会把当前输出重新锚定到约 22.22 ms，仍受 25 ms 上限约束。单次不足不会调整，避免交替解码抖动导致最低提前量与上限反复相撞；因此 16.67 ms 是修正触发阈值，不是每帧都满足的硬保证。此修正不添加等待队列，也不无限累加延迟。

首次带 token 启动时可附加 `--ez renderPacing true`。已经连接时，使用下面的独立动作即可进行同一 APK 的 A/B；不需要获取、打印或再次传入 token，也不会主动断开 TCP 或更改系统显示设置：

```text
adb -s SERIAL shell am start -n com.tablink.client/.MainActivity -a com.tablink.client.SET_RENDER_PACING --ez renderPacing false
adb -s SERIAL shell am start -n com.tablink.client/.MainActivity -a com.tablink.client.SET_RENDER_PACING --ez renderPacing true
adb -s SERIAL shell content query --uri content://com.tablink.client.display/pacing
```

只读 `pacing` 接口仍是单行 `json` 列，不包含认证信息。`decoderId` 标识解码器实例，`epoch` 在开关变化时递增；规划、重锚及丢显示计数在新 epoch 清零。`scheduledFrames` / `immediateFrames` 是输出规划计数，`lateDrops` / `crowdedDrops` / `outOfOrderDrops` 是被放弃的已解码输出，`aheadClamps` 是未来排程触及上限的次数。`actualCallbackFps` 和 `decoderRenderCallbacks` 仍来自实际 `OnFrameRenderedListener`，后者按整个解码器实例累计；不能把计划时间或规划计数当成实际显示 FPS。指标最多每 500 ms 发布一次，切换及停止立即更新。已经提交给 Surface 的旧计划最多还有约 25 ms，做 A/B 时应给切换后留出短暂稳定时间。

`minimumLeadMs` 是上述触发阈值，`insufficientLeadFrames` 统计低于阈值的样本，`leadFloorReanchors` 统计连续不足引起的修正次数；`minimumObservedAheadMs`、`lastAheadMs`、`maximumAheadMs` 是实际规划提前量。偶发不足和真正的时钟修正分开记录。

未带 `renderPacing` 的新电脑端连接默认开启节拍控制，应用退出不保存手动切换值。需要诊断时仍可使用上面的 `false` 动作临时关闭。

2026-09-20，本机 W202DS 使用相同 0.4.2 APK、相同 D3D 动态画面源和原生 1200 × 1920 / 90 Hz 模式完成了约 30 秒 A/B。Windows 使用高精度定时 FFmpeg；两组 SurfaceFlinger 环形记录采样均连续、无覆盖缺口：

| 指标 | A：立即呈现 | B：有界节拍控制 |
| --- | ---: | ---: |
| 采样时长 | 30.49 s | 30.53 s |
| 视频层实际呈现 | 77.665 fps | 89.837 fps |
| 帧间隔 P95 | 22.211 ms | 11.121 ms |
| 帧间隔 P99 | 22.228 ms | 11.137 ms |
| 跳过的 VSYNC 周期 | 378 | 7 |

B 组的解码回调仍约 90 fps，节拍诊断未记录丢显示帧，观察到的最大未来排程为 25 ms。原始证据为发行目录 `diagnostics/pacing-a042-presentation.json`、`diagnostics/pacing-b042-presentation.json` 及对应 `.latency.txt` 文件。这是该设备、该负载下的短时测量，不代表延迟为零或长期始终满帧。

随后默认开启版重新启动时，实际呈现回落至约 77.38 fps，同时解码回调仍约 90 fps、计划提前量下降至约 7.8 ms。因此不能仅以上面的热切换结果作为最终验收。当前构建增加了连续不足时修正时钟的逻辑；已完成纯 JVM 测试，持续动态负载实测结果以根目录 `VERIFICATION.md` 为准。

## 传输协议 v1

TCP 双向数据包格式：`type: uint8` + `length: uint32 big-endian` + `payload: byte[length]`。最大载荷 8 MiB；无效长度、截断包或未知服务端消息会结束当前连接。JSON 使用 UTF-8。

| 方向 | type | 载荷 |
| --- | --- | --- |
| 平板 → 电脑 | `0x10` | 首次扫码登记：`protocol`、五分钟有效且只能成功使用一次的 `token`、含 `trusted-device-v1` 的 `features`、`deviceId`、P-256 SPKI Base64 `devicePublicKey`，以及可选 `deviceName` |
| 平板 → 电脑 | `0x17` | 已登记设备重连 hello：`protocol`、`deviceId` 和含 `trusted-device-v1` 的 `features`；不含 token |
| 电脑 → 平板 | `0x18` | 新挑战：`protocol`、`feature`、`hostId`、`deviceId` 和 32 字节挑战的 Base64 |
| 平板 → 电脑 | `0x19` | `deviceId` 与 Keystore P-256 / SHA-256 ECDSA DER 签名的 Base64 |
| 电脑 → 平板 | `0x1a` | 首次登记确认：`protocol`、`feature`、`hostId`、`deviceId`；收到后才保存公开电脑信任元数据 |
| 电脑 → 平板 | `0x01` | 完整 JPEG 图像 |
| 电脑 → 平板 | `0x02` | `{"protocol":1,"features":["render-submitted-v1","decoder-refresh-v1","receiver-feedback-v1","adaptive-video-v1"],"width":1280,"height":720,"message":"...","capturePaused":true}`；只回显实际协商的能力，能力与暂停字段可选 |
| 电脑 → 平板 | `0x03` | UTF-8 错误文本，显示后停止自动重连 |
| 平板 → 电脑 | `0x11` | `{"kind":"down|move|up|scroll","x":0.5,"y":0.5,"delta":120}` |
| 平板 → 电脑 | `0x12` | `{"kind":"frame-presented","sequence":21,"width":1280,"height":800}` |
| 平板 → 电脑 | `0x13` | 与能力查询接口相同的屏幕模式 JSON |
| 平板 → 电脑 | `0x14` | 协商后发送的 `render-submitted` 进度：累计 `frames`、本帧 `ptsUs`、尺寸、`fps` 和 `decoder`；不推进呈现回调计数 |
| 平板 → 电脑 | `0x15` | 协商 `decoder-refresh-v1` 后发送的 8 字节大端正整数恢复代次；请求当前会话的下一枚新 IDR，不代表呈现成功 |
| 平板 → 电脑 | `0x16` | 协商 `receiver-feedback-v1` 后发送的独立 JSON 反馈：递增 sequence、接收帧/字节、decoder/recovery epoch、队列深度/容量、提交、呈现和各类丢弃累计值 |
| 电脑 → 平板 | `0x20` | `{"codec":"video/avc","width":1200,"height":1920,"fps":90,"csd0":"BASE64_SPS","csd1":"BASE64_PPS","bitrateKbps":12000,"generation":2,"encoder":"nvenc"}`；末三项及其他诊断字段可选、可忽略 |
| 电脑 → 平板 | `0x21` | 8 字节大端非负 `ptsUs`，后接一个完整 Annex-B H.264 access unit |

原生网络会话在 `0x10` 登记成功，或完成 `0x17` → `0x18` → `0x19` 挑战认证后，才发送 `0x13` 屏幕参数。Android 不把发现响应当作认证，不导出 Keystore 私钥，也不保留首次二维码 token。电脑撤销设备、host ID 或固定证书不匹配、挑战签名错误时，连接在显示准备和视频配置之前结束。

`0x12` 是客户端画面呈现回调进度。JPEG 在成功解码、绘制且 `unlockCanvasAndPost` 正常返回后计数；H.264 只在 `MediaCodec.OnFrameRenderedListener` 通知后计数。首次立即确认，此后有新进展时约每秒确认一次；重绘旧 JPEG 不重复计数，每次 TCP 连接从 1 重新计数。新增 `fps`、`codec`、`decoder`、`droppedFrames` 字段，其中 FPS 由回调时间戳测量。回调可能延迟、成批或少报，不能替代最终可见帧率和实机验收。

`0x14` 只证明某个访问单元已经成功排入 MediaCodec 输入队列。每个 TCP 会话累计 `frames`；第一次提交立即报告，之后最多约每秒一次。解码器重配会保留会话累计帧数；0.8.3 的同连接自适应重配也保持媒体 PTS 严格递增，只有真正重连才重新建立会话计数与媒体时钟。单调回调时钟必须前进，尺寸必须有效，decoder 名称最多 160 字符。暂停采集或能力未协商时不发送。电脑端把 submitted 与 presented 的 FPS、期限和健康阶段分别处理，绝不把 `0x14` 当作 `0x12`。

`0x16` 与 `0x14`、`0x12` 独立：只要 decoder 活动且队列容量有效，即使提交或呈现暂时停滞，客户端仍最多每秒发送一份最新反馈。该报告只作为自动码率和诊断输入，不会伪造提交、呈现或用户可见画面的健康证据。队列溢出或帧等待超过 150 ms 导致健康参考链丢失时，客户端为新的 recovery epoch 生成限频 `0x15`；首次等待 IDR、主动清空、decoder 重配和正常关闭不会产生队列恢复请求。

H.264 必须先发 `0x20` 配置，SPS/PPS 分别为带 Annex-B 起始码的 Base64 字节；随后 `0x21` 中 PTS 严格递增，B 帧必须禁用，首个访问单元和配置变化后的首个访问单元必须是带 SPS/PPS 的 IDR。只有双方同时协商 `receiver-feedback-v1` 与 `adaptive-video-v1`，新版电脑端才会为自动、低延迟、均衡或高清晰画质计划切换而在同一 TCP 会话中重复发送 `0x20`；原生方向、分辨率与请求刷新率保持不变。旧电脑端或未完整协商的会话保持固定画质计划，但在安全桌面、DDA 或采集恢复后仍可按既有协议重发同一计划的 `0x20`，客户端继续接受这种兼容重配。客户端按尺寸、目标帧率、性能点、低延迟能力和本进程失败记录为 AVC decoder 排序，优先硬解，并保留其他硬解及软件 decoder 作为兜底。输入队列最多 6 帧；排队超过 150 ms 或队列溢出会放弃相关依赖链并等待新 IDR，避免无限积累延迟。配置变化会重建解码器，Surface 销毁时释放并安全重连。

某个 MediaCodec 在创建、配置、启动或运行中失败时，客户端会在同一个 TLS/TCP 会话和同一块副屏上尝试下一候选。备用 decoder 成功启动后才进入“等待关键帧”状态并发送一次 `0x15`；电脑端暂停该会话的 P 帧，等编码器自然产生下一枚新 IDR，再在同一个 `0x21` 中补齐缺失的 SPS/PPS。电脑不会重启编码器、重新认证、重新安装驱动或新建副屏。只有备用 decoder 的真实 `OnFrameRenderedListener` 回调才结束等待状态；`0x14` 提交不算恢复成功。旧电脑端未回显该能力时，客户端不会发送未知消息，只等待原视频的自然 IDR。

电脑暂时无法采集桌面（例如安全桌面正在使用）时，可在 `0x02` 中发送 `capturePaused:true` 和简短 `message`。客户端保留同一 TCP 连接、Surface、解码器和最后画面，只显示暂停提示，不将心跳当成新画面或推进 `0x12`。电脑必须持续发送间隔短于 15 秒的状态心跳，并在其守护逻辑中区分这种已确认的暂时暂停。普通状态包缺少 `capturePaused` 时保留原暂停状态，明确 `false` 才清除暂停提示。

恢复时可继续原视频，也可按既有协议重发 `0x20` 建立新解码器再发送新 IDR；完整协商自适应能力后，新版电脑端还可用相同机制切换画质计划。只要仍是同一 TCP 会话，Windows 的连接级媒体时钟保证新配置后的 PTS 大于此前所有 PTS，客户端的 `0x12 sequence` 也继续递增，不重置 `PresentationProgress`。真正关闭或重建 TCP 连接才重置这些会话状态。这个扩展向后兼容已有协议，不绕过 Windows 安全桌面的采集限制。

`delta` 仅用于 scroll，每个标准滚轮刻度为 120，与 Windows `WHEEL_DELTA` 一致。`x`、`y` 为画面范围内的 0–1 坐标；图片使用 contain 缩放，四周黑边不产生点击。单指移动最多约每 16 ms 发送一次；拖动离开画面、触摸取消或被系统中断时会发送 up。第二根手指不会产生额外鼠标按下。

电脑端必须在连接中断和会话结束的 finally 路径中释放尚未松开的左键。网络断开时客户端无法保证最后一个 up 包送达。

为限制解码内存，JPEG 宽高分别不超过 8192，像素总数不超过 1600 万。图像的尺寸以 JPEG 解码结果为准，不信任状态 JSON 提供的尺寸。Socket 读取超时 15 秒，电脑端应持续提供帧或状态包。

网络读取、JPEG 解码与 Surface 绘制在工作线程运行，鼠标发送使用独立工作线程。绘制完成后回收旧帧；只保留当前帧用于横竖屏和 Surface 重建。

## 已验证范围

本节保留较早版本已完成的安卓逻辑、构建与实机证据，便于回归比较。0.8.8 的 JVM、debug/release preview、lint、签名、W202DS 和公共资产结果已经固定在根目录 `VERIFICATION-0.8.8.md`；当前 0.8.9 / build 21 候选必须按 `VERIFICATION-0.8.9.md` 重新验证，不能借用 0.8.8 的产物或实机结论。

最终 0.4.2（APK SHA-256 `4F6FBBD8D22447A1D2702B2028A4868CC779D89124074E8923BBE667DFCD58FC`）已通过正常安装、默认开关开启的长时间真机验证：Windows 控制窗口最小化，原生 1200×1920 / 90 Hz，120.433 秒实际呈现 **89.702 fps**，四段 30 秒为 89.800 / 89.667 / 89.733 / 89.567 fps，P99 11.147 ms，最大间隔 33.295 ms，没有断线或采样覆盖缺口。该结果来自 SurfaceFlinger 实际呈现时间戳，而非计划帧率。完整方法和保留的未通过候选结果见根目录 `VERIFICATION.md` 与发行目录 `diagnostics/final042-driftfixed-*`。

- 0.8.8 Preview 1 新增可信设备协议、Android Keystore P-256 身份、只含公开元数据的电脑信任记录和局域网地址发现；旧 bearer `pairing.lastLink` 在启动时删除。0.8.5 的能力协商、队列 recovery epoch、关键帧限频、接收端反馈和 decoder 候选行为保持。纯 JVM 测试不运行真实 Android Keystore、MediaCodec 或 UDP 广播；这些边界必须由最终 W202DS 验证补齐。
- 20 项纯 JVM HUD / 暂停状态断言：透明度与不透明度方向、持久化数值边界、九宫格位置、颜色格式，以及暂停、普通心跳、恢复和同会话序号延续。0.5.0 的设置手势、沉浸显示和电脑采集暂停恢复仍需真机联合验证；不能用这些逻辑测试替代运行中的画面验收。
- 26,024 项独立纯 JVM RenderClock 断言覆盖稳定 90 fps、解码抖动、较慢输入、首批突发、长停顿、固定硬件流水线延迟、重复/倒序 PTS、极大 PTS 跳变、重连重置，以及不同帧率下未来排程不超过 25 ms。新增 100 秒缓慢时钟偏移、持续到达延迟和正负 5 ms 交替抖动用例；后者检查计划间隔均匀且不会反复触发上下限修正。逻辑测试仅验证时钟行为，不能替代最终实际呈现率验收。
- 当前 0.8.9 源码已通过 Android JVM、`assembleDebug` / `lintDebug`、`assembleRelease` / `lintRelease`、v1/v2 签名与跨平台版本契约；本地 Release Preview 也通过固定 signer、包名、`versionName 0.8.9` 和 `versionCode 21` 门禁。精确产物与边界记录在根目录 `VERIFICATION-0.8.9.md`；W202DS 覆盖安装和最终公共 APK 回下载仍待验证。
- 本地门禁不能代替公共下载副本验证。0.8.9 最终公开 APK 的字节数和 SHA-256 只有在 GitHub Release 建立并从公开地址回下载复核后才能填写；当前公开 APK 仍是 0.8.8 Preview 1。
- 0.4.0 已在本机 W202DS 上显示 1200 × 1920 独立 USB 桌面，解码器实际为 `c2.unisoc.avc.decoder`。当时系统将物理屏幕固定在 60 Hz，解码回调约 63–65 fps，单次 SurfaceFlinger 实际呈现采样约 50.4 fps；这些数字不是同一指标。
- 0.4.1 已安装并完成原生 1200 × 1920、物理屏幕 90 Hz 验证。2026-09-20 02:26 的三个只读样本中，能力接口均报告实际模式 2 / 90 Hz；SurfaceFlinger 周期为 11,111,111 ns，显示策略固定 90 Hz。此时中兴“锁定刷新率”已开启，应用亮度补偿未启用。
- 同次旧 FFmpeg 采样的硬件解码回调为 63.37–64.62 fps，视频层实际呈现为 56.57–59.88 fps。随后 Windows 高精度 FFmpeg 到位，0.4.1 在 02:53 的三个短样本中达到 89.71–90.36 解码 fps、83.39–88.65 实际呈现 fps；物理面板仍为 90 Hz。这些是不同指标，且不代表持续满帧。两轮证据分别位于发行目录 `diagnostics/android-panel-90hz-verification.json` 和 `diagnostics/android-custom-ffmpeg-90hz-verification.json`，各自附有 `-latency.txt` 原始时间戳。动态负载的整体真机验收结果以根目录 `VERIFICATION.md` 为准。

实现参考：[Android SurfaceHolder](https://developer.android.com/reference/android/view/SurfaceHolder)、[MediaCodec](https://developer.android.com/reference/android/media/MediaCodec)、[刷新率请求](https://developer.android.com/media/optimize/performance/frame-rate)、[窗口亮度](https://developer.android.com/reference/android/view/WindowManager.LayoutParams#screenBrightness)、[沉浸全屏](https://developer.android.com/develop/ui/views/layout/immersive)、[返回手势](https://developer.android.com/guide/navigation/custom-back/predictive-back-gesture)、[Android 调试桥](https://developer.android.com/tools/adb)。
