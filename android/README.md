# TabLink Android 客户端 0.8.2 预览版

作者：**张林杰（Jey / [@linjierd](https://github.com/linjierd)）** · 博客：[Linjie / 开发笔记](https://linjie.space/)

供 Windows TabLink 通过 Wi-Fi、USB 网络共享或既有 ADB 通道连接的安卓平板客户端。包名 `com.tablink.client`，启动组件 `com.tablink.client/.MainActivity`。支持 Android 6.0（API 23）及以上；当前构建目标为 Android 15（API 35）。

Wi-Fi 与 USB 网络共享使用相同的 TLS 配对通道，无需 ADB 或 USB 调试。既有 ADB 通道仍仅通过 `127.0.0.1` 明文连接，由电脑端对用户选定设备建立 `adb reverse`。APK 不枚举或切换 USB 设备模式，不使用 Android Open Accessory 模式；“打开 USB 网络共享设置”只打开系统设置页面，由用户操作共享开关。

当前优先使用 H.264 硬件解码，并兼容旧版 JPEG 画面。启动即进入沉浸全屏，没有常驻控制条或全屏按钮。支持原比例显示、当前方向与屏幕模式上报、保持屏幕常亮、重连、退出，以及单指鼠标点击和拖动。当前没有双指滚动、音频、压感笔或键盘输入；电脑端负责视频编码，安卓端负责解码。

真正的扩展桌面由 Windows 虚拟显示驱动提供；本 APK 负责显示电脑端选定的桌面画面，本身不能创建 Windows 显示器。

## 无需 USB 调试的连接

正常打开应用会看到“扫描电脑二维码”“粘贴连接链接”“打开 USB 网络共享设置”。Wi-Fi 连接时，平板和电脑应处于同一局域网；USB 连接时，接好数据线并在系统设置开启 USB 网络共享，然后扫描电脑端显示的对应 IPv4 地址二维码。没有相机、拒绝相机权限或相机被占用时，仍可粘贴链接。

二维码只包含当前会话的连接凭证，不包含显示器尺寸或刷新率：

```text
tablink://connect?host=<IPv4>&port=27184&token=<64 lowercase hex>&cert=<64 hex SHA256 DER>
```

客户端拒绝未知或重复参数、额外路径和片段、DNS 名称、IPv6、非规范 IPv4、环回/多播地址、错误端口，以及不符合长度或字符要求的 token/证书指纹。0.8.0 支持八个原生会话端口：`27184`、`27186`、`27187`、`27188`、`27189`、`27190`、`27191`、`27192`；`27185` 专门保留给浏览器 HTTPS，原生客户端拒绝此端口。配对、保存与重连完整保留二维码中的会话端口，不改回默认端口。其他端口及带前导零、正负号、百分号编码等非规范形式均拒绝。证书指纹允许十六进制大小写，token 只允许小写。

网络连接只启用 TLS 1.2/1.3。`PinnedTls.CertificatePin` 使用常量时间比较验证服务端叶证书完整 DER 的 SHA-256 必须等于二维码的 `cert`，同时检查证书有效期。这里使用二维码中的固定证书作为身份依据，不能用系统可信 CA 的另一张证书替代，也没有跳过验证的回退路径。证书校验失败时不会发送认证包，界面提示检查日期并重扫当前二维码。

TLS 握手成功后，客户端在 `0x10` 认证中声明 `render-submitted-v1` 可选能力，再同步发送 `0x13` 当前原生显示能力和请求刷新率，然后才读取和解码视频。只有电脑端在 `0x02` 状态中回显同一能力后，客户端才会发送 `0x14` 解码提交进度；旧电脑端不回显时继续按原协议工作。旋转或显示模式变化仍会更新 `0x13`；若电脑端因此关闭当前 TCP，客户端沿用当前 URI 的地址、证书和凭证自动重连，不要求重新扫码。电脑端停止或重新创建网络会话后，旧配对失效，应扫描新码。

首帧实际呈现后，配对链接保存在应用私有 `SharedPreferences("pairing", MODE_PRIVATE)`。正常启动不会自动使用它；用户可点击“重连上次电脑”。显示设置中可以“忘记上次配对的电脑”，或“更换连接 / 扫描二维码”。Token 不写入应用日志、状态文字或公开的显示能力接口。

扫码由独立、未导出的 `QrScannerActivity` 使用原生相机和内嵌 ZXing core 完成；不依赖 Google Play、第三方扫码应用或网络识别服务。相机只在用户打开扫码页面后申请权限，图像仅在内存中处理，离开页面后释放相机。ZXing 的来源、校验值和 Apache-2.0 许可见 `THIRD_PARTY_NOTICES.md`，许可文本也打包在 APK 的 `assets/licenses` 中。

## 全屏观看与统计设置

观看时只保留统计文字。默认白色、透明度 30%，即不透明度 70%，背景完全透明。**长按统计文字，或使用 Android 返回手势 / 返回键**，即可打开设置；系统导航栏可通过边缘滑动临时显示。

设置内可选择九宫格位置、五种常用文字颜色或输入自定义 `#RRGGBB`，并调整 0–100% 透明度。设置明确同时显示透明度和不透明度，立即生效并保存到应用偏好；下次打开仍保留。100% 透明会隐藏文字，此时使用返回手势进入设置。

设置面板中的“完成”关闭面板继续观看，“重连”重新建立当前网络或 ADB 会话，“退出”停止连接并关闭应用。单纯打开或调整显示设置不停止视频、不重建解码器；点击“更换连接”才回到连接面板。原生分辨率、刷新率请求和 0.4.2 已验证的呈现节拍算法保持不变。

## 正式版自动更新

0.8.0 在进入前台时立即读取电脑端同一套签名稳定版清单，应用保持打开期间每 6 小时复查。默认开启自动下载；设置面板可以关闭自动下载，或手动检查、继续和重试。投屏期间只检查，不下载或安装；停止投屏后继续。

发布构建必须通过 `-UpdateManifestUrl` 注入 HTTPS 清单地址。客户端先验证内置 P-256 公钥对应的签名，再核对 stable SemVer、versionCode、包名、APK 签名、大小和 SHA-256。安装使用 Android `PackageInstaller`：Android 12 及以上会请求无需用户操作，但系统仍可要求显示标准确认页。当前直接分发包继续使用既有开发签名，以便已安装的平板原地更新；切换到新的商店签名前必须单独安排签名迁移。

## 构建

要求 JDK 17 或 21、Gradle 8.13、Android SDK platform 35 和完整的 build-tools 35.0.0。Android Gradle Plugin 固定为 8.13.2。二维码依赖固定为 Maven Central 的 ZXing core 3.5.3；首次构建需要下载依赖，缓存完整后才可使用 `-Offline`。

在本机项目目录中，以下命令会运行协议与坐标测试、编译 APK、执行 Android Lint、校验 APK 签名：

```powershell
Set-Location '<repository-root>\android'
.\build.ps1 -UpdateManifestUrl 'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json'
```

本机构建采用的精确工具参数：

```powershell
.\build.ps1 `
  -JavaHome '<path-to-jdk-21>' `
  -AndroidSdk '<path-to-android-sdk>' `
  -Gradle '<path-to-gradle-8.13>\bin\gradle.bat' `
  -UpdateManifestUrl 'https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json' `
  -Offline # 仅在 Gradle 与 Maven 依赖已经完整缓存时使用
```

其他电脑可通过三个参数指定工具路径；首次需要下载 Gradle 插件依赖时省略 `-Offline`。脚本只在当前进程设置 Java/SDK 环境变量，并在退出时恢复。

普通构建输出 `artifacts/TabLink-android-0.8.2-debug.apk`。增加 `-ReleasePreview` 会运行 `assembleRelease` / `lintRelease`，输出不可调试的 `artifacts/TabLink-android-0.8.2-preview.apk`。两者均为 `versionCode 13`，并使用本机生成且被 Git 忽略的 `build/signing/debug.keystore` 开发证书，以便覆盖早期 TabLink 测试安装；它不是应用商店生产签名。应安全保留同一份签名文件，绝不能把私钥提交到仓库。

本机构建时发现系统 SDK 的 build-tools 35.0.0 目录只有未完成安装记录，因此在项目 `.tools/sdk` 中准备了隔离 SDK：复制现有 SDK platform 35，并从 [Google Android 官方仓库](https://dl.google.com/android/repository/build-tools_r35_windows.zip) 下载 build-tools 35.0.0。压缩包使用 [官方 repository 元数据](https://dl.google.com/android/repository/repository2-1.xml) 中 SHA-1 `af059bb67cf7786f45ee0db85e2d24985df1b4b6` 校验。没有修改系统 SDK。`.tools`、`.gradle`、`build` 和 `app/build` 属于本地构建工具或缓存，不应放进用户发行包。

## 电脑端调用

电脑端应先检查用户选定的设备未被排除，并且是获准使用的 USB 调试设备。以下命令中的 `SERIAL` 必须是该设备的真实序列号；不要批量对所有设备执行。

```text
adb -s SERIAL install -r TabLink-android-0.8.2-preview.apk
adb -s SERIAL reverse --no-rebind tcp:27183 tcp:27183
adb -s SERIAL shell am start -n com.tablink.client/.MainActivity --es host 127.0.0.1 --ei port 27183 --es token RANDOM_SESSION_TOKEN
```

以上命令只用于原有 ADB 兼容通道。`token` 是每次会话由电脑端生成的随机凭证；不要使用示例常量或将凭证记录到日志。`host` 可省略，其他主机地址会被拒绝；ADB 端口范围为 1024–65535，默认 27183。应用使用 `singleTask`，电脑端再次启动可替换旧会话。网络连接使用上述配对 URI 的 `ACTION_VIEW`，不会把非环回地址传入明文 ADB 通道。

本机中兴安装器可能使 ADB 安装停在等待确认。此时应从平板正常的文件管理器或已获准安装应用的浏览器打开对应 APK，按标准更新界面完成安装，再核对实际 versionCode；旧的“安装完成”页面不能证明新版本已安装。不需要关闭安装验证器或更改全局安全设置。

应用进入后台、用户点击退出或新会话取代旧会话时会关闭连接、结束工作线程并释放位图。恢复前台时使用当前启动参数连接。正常断线后按 1、2、4、8、10 秒间隔自动重试；成功收到画面后重置退避。收到电脑端错误包后停止自动重试，由用户或电脑端重新发起连接。

## 屏幕能力与刷新率

只读接口 `content://com.tablink.client.display/capabilities` 返回单行 `json` 列。它仅公开显示硬件指标，不提供文件、连接 token 或设备控制功能。

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

角落的“解码 xx fps”来源于解码器实际渲染回调，不等于物理屏幕最终可见帧率。系统合成器可以合并或丢弃帧；应同时看当前屏幕 Hz，需要精确验收时另查 SurfaceFlinger 的实际呈现时间。

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
| 平板 → 电脑 | `0x10` | 首包：`{"protocol":1,"token":"...","features":["render-submitted-v1"]}`；`features` 可选 |
| 电脑 → 平板 | `0x01` | 完整 JPEG 图像 |
| 电脑 → 平板 | `0x02` | `{"protocol":1,"features":["render-submitted-v1"],"width":1280,"height":720,"message":"...","capturePaused":true}`；能力与暂停字段可选 |
| 电脑 → 平板 | `0x03` | UTF-8 错误文本，显示后停止自动重连 |
| 平板 → 电脑 | `0x11` | `{"kind":"down|move|up|scroll","x":0.5,"y":0.5,"delta":120}` |
| 平板 → 电脑 | `0x12` | `{"kind":"frame-presented","sequence":21,"width":1280,"height":800}` |
| 平板 → 电脑 | `0x13` | 与能力查询接口相同的屏幕模式 JSON |
| 平板 → 电脑 | `0x14` | 协商后发送的 `render-submitted` 进度：累计 `frames`、本帧 `ptsUs`、尺寸、`fps` 和 `decoder`；不推进实际呈现计数 |
| 电脑 → 平板 | `0x20` | `{"codec":"video/avc","width":1200,"height":1920,"fps":90,"csd0":"BASE64_SPS","csd1":"BASE64_PPS"}` |
| 电脑 → 平板 | `0x21` | 8 字节大端非负 `ptsUs`，后接一个完整 Annex-B H.264 access unit |

`0x12` 是客户端画面实际呈现回调进度。JPEG 在成功解码、绘制且 `unlockCanvasAndPost` 正常返回后计数；H.264 只在 `MediaCodec.OnFrameRenderedListener` 通知后计数。首次立即确认，此后有新进展时约每秒确认一次；重绘旧 JPEG 不重复计数，每次 TCP 连接从 1 重新计数。新增 `fps`、`codec`、`decoder`、`droppedFrames` 字段，其中 FPS 由回调时间戳测量。回调可能延迟、成批或少报，不能替代最终可见帧率和实机验收。

`0x14` 只证明某个访问单元已经成功排入 MediaCodec 输入队列。每个 TCP 会话累计 `frames`；第一次提交立即报告，之后最多约每秒一次。解码器重配会保留会话累计帧数，并允许媒体 PTS 从零重新开始；重连才重置累计值。单调回调时钟必须前进，尺寸必须有效，decoder 名称最多 160 字符。暂停采集或能力未协商时不发送。电脑端把 submitted 与 presented 的 FPS、期限和健康阶段分别处理，绝不把 `0x14` 当作 `0x12`。

H.264 必须先发 `0x20` 配置，SPS/PPS 分别为带 Annex-B 起始码的 Base64 字节；随后 `0x21` 中 PTS 严格递增，建议禁用 B 帧并至少每秒发送一个 IDR。客户端只选择硬件 AVC 解码器，以独立 Surface 显示，避免与 JPEG Canvas 生产者冲突。输入队列最多 6 帧；排队超过 150 ms 或队列溢出会放弃相关依赖链并等待新 IDR，避免无限积累延迟。配置变化重建解码器，Surface 销毁时释放并安全重连。

电脑暂时无法采集桌面（例如安全桌面正在使用）时，可在 `0x02` 中发送 `capturePaused:true` 和简短 `message`。客户端保留同一 TCP 连接、Surface、解码器和最后画面，只显示暂停提示，不将心跳当成新画面或推进 `0x12`。电脑必须持续发送间隔短于 15 秒的状态心跳，并在其守护逻辑中区分这种已确认的暂时暂停。普通状态包缺少 `capturePaused` 时保留原暂停状态，明确 `false` 才清除暂停提示。

恢复时可继续原视频，或重新发送 `0x20` 建立新解码器后发送新 IDR；新配置的 PTS 可以从 0 开始。只要仍是同一 TCP 会话，客户端的 `0x12 sequence` 会继续递增，不重置 `PresentationProgress`。真正关闭或重建 TCP 连接才重置该序号。这个状态扩展向后兼容已有协议，不绕过 Windows 安全桌面的采集限制。

`delta` 仅用于 scroll，每个标准滚轮刻度为 120，与 Windows `WHEEL_DELTA` 一致。`x`、`y` 为画面范围内的 0–1 坐标；图片使用 contain 缩放，四周黑边不产生点击。单指移动最多约每 16 ms 发送一次；拖动离开画面、触摸取消或被系统中断时会发送 up。第二根手指不会产生额外鼠标按下。

电脑端必须在连接中断和会话结束的 finally 路径中释放尚未松开的左键。网络断开时客户端无法保证最后一个 up 包送达。

为限制解码内存，JPEG 宽高分别不超过 8192，像素总数不超过 1600 万。图像的尺寸以 JPEG 解码结果为准，不信任状态 JSON 提供的尺寸。Socket 读取超时 15 秒，电脑端应持续提供帧或状态包。

网络读取、JPEG 解码与 Surface 绘制在工作线程运行，鼠标发送使用独立工作线程。绘制完成后回收旧帧；只保留当前帧用于横竖屏和 Surface 重建。

## 已验证范围

最终 0.4.2（APK SHA-256 `4F6FBBD8D22447A1D2702B2028A4868CC779D89124074E8923BBE667DFCD58FC`）已通过正常安装、默认开关开启的长时间真机验证：Windows 控制窗口最小化，原生 1200×1920 / 90 Hz，120.433 秒实际呈现 **89.702 fps**，四段 30 秒为 89.800 / 89.667 / 89.733 / 89.567 fps，P99 11.147 ms，最大间隔 33.295 ms，没有断线或采样覆盖缺口。该结果来自 SurfaceFlinger 实际呈现时间戳，而非计划帧率。完整方法和保留的未通过候选结果见根目录 `VERIFICATION.md` 与发行目录 `diagnostics/final042-driftfixed-*`。

- 0.8.2 的协议断言覆盖能力协商、解码提交限频与累计、解码器重建、非法尺寸和旧回调保护；最终断言数量以 `VERIFICATION-0.8.2.md` 中本次构建输出为准。
- 20 项纯 JVM HUD / 暂停状态断言：透明度与不透明度方向、持久化数值边界、九宫格位置、颜色格式，以及暂停、普通心跳、恢复和同会话序号延续。0.5.0 的设置手势、沉浸显示和电脑采集暂停恢复仍需真机联合验证；不能用这些逻辑测试替代运行中的画面验收。
- 26,024 项独立纯 JVM RenderClock 断言覆盖稳定 90 fps、解码抖动、较慢输入、首批突发、长停顿、固定硬件流水线延迟、重复/倒序 PTS、极大 PTS 跳变、重连重置，以及不同帧率下未来排程不超过 25 ms。新增 100 秒缓慢时钟偏移、持续到达延迟和正负 5 ms 交替抖动用例；后者检查计划间隔均匀且不会反复触发上下限修正。逻辑测试仅验证时钟行为，不能替代最终实际呈现率验收。
- `assembleDebug` 成功，`lintDebug` 无错误。Lint 仍提示目标 SDK 版本、较新 XML 属性和中文界面可翻译性等兼容/维护警告。
- 使用 SDK `apksigner` 验证 APK 签名，并输出 SHA-256。
- 0.4.0 已在本机 W202DS 上显示 1200 × 1920 独立 USB 桌面，解码器实际为 `c2.unisoc.avc.decoder`。当时系统将物理屏幕固定在 60 Hz，解码回调约 63–65 fps，单次 SurfaceFlinger 实际呈现采样约 50.4 fps；这些数字不是同一指标。
- 0.4.1 已安装并完成原生 1200 × 1920、物理屏幕 90 Hz 验证。2026-09-20 02:26 的三个只读样本中，能力接口均报告实际模式 2 / 90 Hz；SurfaceFlinger 周期为 11,111,111 ns，显示策略固定 90 Hz。此时中兴“锁定刷新率”已开启，应用亮度补偿未启用。
- 同次旧 FFmpeg 采样的硬件解码回调为 63.37–64.62 fps，视频层实际呈现为 56.57–59.88 fps。随后 Windows 高精度 FFmpeg 到位，0.4.1 在 02:53 的三个短样本中达到 89.71–90.36 解码 fps、83.39–88.65 实际呈现 fps；物理面板仍为 90 Hz。这些是不同指标，且不代表持续满帧。两轮证据分别位于发行目录 `diagnostics/android-panel-90hz-verification.json` 和 `diagnostics/android-custom-ffmpeg-90hz-verification.json`，各自附有 `-latency.txt` 原始时间戳。动态负载的整体真机验收结果以根目录 `VERIFICATION.md` 为准。

实现参考：[Android SurfaceHolder](https://developer.android.com/reference/android/view/SurfaceHolder)、[MediaCodec](https://developer.android.com/reference/android/media/MediaCodec)、[刷新率请求](https://developer.android.com/media/optimize/performance/frame-rate)、[窗口亮度](https://developer.android.com/reference/android/view/WindowManager.LayoutParams#screenBrightness)、[沉浸全屏](https://developer.android.com/develop/ui/views/layout/immersive)、[返回手势](https://developer.android.com/guide/navigation/custom-back/predictive-back-gesture)、[Android 调试桥](https://developer.android.com/tools/adb)。
