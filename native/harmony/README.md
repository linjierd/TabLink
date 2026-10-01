# TabLink 原生 HarmonyOS NEXT 接收端

作者：**张林杰（Jey / [@linjierd](https://github.com/linjierd)）** · 博客：[Linjie / 开发笔记](https://linjie.space/)

这是可在 DevEco Studio 打开的 **ArkUI + C++ AVCodec 源码工程**。它没有 WebView、HTML 或浏览器解码层。当前版本元数据为 `0.8.0`、Harmony `versionCode 800`。当前提交尚未经过 Harmony SDK 编译、签名、安装、应用市场跳转或鸿蒙真机验证；本机没有 DevEco Studio / HarmonyOS SDK，也没有连接的 HarmonyOS NEXT 设备。因此此目录不提供 HAP，不代表已有可安装、已验收的鸿蒙版本。

## 已实现的代码路径

- ArkUI 连接页面，粘贴电脑为该设备生成的 `tablink://connect?...` 信息；不扫描或更改任何 USB 设备，不改变路由、DNS 或系统网络设置。
- Network Kit TLS 1.2/1.3 连接。取得远端 X.509 叶证书并计算 DER SHA-256，与连接信息中的 `cert` 完整比对；通过后才发送令牌和显示 profile。连接信息只在内存中保存，停止时释放，不写日志或文件。
- 全屏原生 `XComponent` Surface，使用其生命周期 Surface ID 创建 NativeWindow。AVCapability 明确选择 H.264 **硬件**解码器；没有可用硬解时报告失败，不静默回退为软件或 WebView。
- C++ 工作线程处理 SPS/PPS、带 PTS 的 Annex-B 访问单元、异步输入/输出缓冲区和 Surface 提交。队列超过 12 个访问单元 / 16 MiB 就结束连接，避免越来越大的延迟；不随意丢弃依赖前帧的 H.264 数据。
- HELLO 声明 `render-submitted-v1`；只有电脑端在当前连接的状态消息中回显该能力后，客户端才会上报 `0x14` 解码提交统计。停止、退出、进入后台、Surface 销毁时结束 socket 和解码器。旋转后重新读取物理像素尺寸并用原配对信息重新连接同一电脑会话。本工程尚未生成或发布 HAP，因此没有绕过能力协商的历史客户端例外。
- 严格限定原生端口 `27184, 27186, 27187, 27188, 27189, 27190, 27191, 27192`，拒绝浏览器端口 `27185`。增量分包器为每个载荷只分配一次缓冲，按实际 TCP 分片填充；包头、SPS/PPS、Annex-B、帧时间戳和尺寸均验证后才进入解码器。
- 同一连接更换解码配置后，必须等新解码器确有有效输出 PTS 才继续上报；不会把先前累计的帧数与初始 `ptsUs=-1` 混在一起。电脑报告采集暂停期间不虚构提交进度，也不因普通的无新帧超时提前断开。

## stable 正式版更新

应用启动、回到前台以及应用内定时器会检查固定的 HTTPS 清单地址：

```text
https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json
```

外层只能是 `{payload,signature}`，其中 `payload` 是原始 UTF-8 JSON 的标准 Base64，`signature` 是 P-256 / SHA-256 的 ASN.1 DER ECDSA 签名。客户端固定内置 `updates/stable-public-key.spki.base64` 对应的 X.509 SPKI 公钥；签名不通过、字段重复、未知字段、非 `stable`、预发行 SemVer、非整秒 `yyyy-MM-ddTHH:mm:ssZ` 发布时间、平台重复、非 HTTPS 地址、越界大小等情况全部失败关闭。签名通过后才解析 artifact。设备会持久保存随机 cohort，按 `SHA-256(cohort + "\n" + releaseId)` 应用 0–100 灰度比例；`0` 是有效的暂停发布值且不会选择更新。

若签名清单没有 `harmony` artifact，连接面板安静显示“尚未发布”；存在版本号和构建号都较新的正式版时，只接受同一签名负载里的 AppGallery HTTPS URL，或正式 `https://linjie.space/download/api/download?path=TabLink%2Fstable%2F...` 跳转 URL。正式跳转必须是精确路径、恰好一个 `path` 参数、没有额外或重复参数。系统 `viewData` handler 负责打开应用市场。客户端**不下载、不侧载、不自行替换 HAP**，安装和应用市场自动更新由 AppGallery / 系统设置负责。投屏期间不弹模态更新对话框，不中断连接；更新状态只在未连接的连接面板显示。

`StableUpdateCrypto.ets` 单独封装 Crypto Architecture Kit 的 `ECC256` SPKI 转换和 `ECC256|SHA256` 验签，任何 SDK、DER 或签名格式错误都拒绝更新。Windows 本机只能用 Node 验证同一 fixture 的 P-256 签名和协议策略；**尚未在 API 12 SDK 验证 `convertKey` 对固定 SPKI、`Verify` 对 ASN.1 DER ECDSA 签名以及 AppGallery handler 的真机兼容性**。必须在 DevEco 编译并用正式签名真机验证后才可发布。

## 证据含义：提交不是呈现

`OH_VideoDecoder_RenderOutputBuffer` 成功表示解码输出已提交给 Surface，**不能证明该帧已被屏幕合成器实际显示**。本项目只在该调用成功，而且输出 PTS 对应先前成功推入解码器的视频帧后增加 `submittedFrames`。

客户端从不发送 `0x12` 呈现 ACK，界面明确显示“解码提交 … fps · 实际呈现待验证”。Windows 主机必须支持单独的 `0x14` 协议及其存活检查；主机不能将该统计累加到 `PresentedFrames`。真实亮屏效果、丢帧、端到端延迟、60/90/120 Hz 等仍需设备测试。已解码/已提交的数字不能用作实际呈现帧率证明。

## 构建与运行条件

1. 安装官方 DevEco Studio，以及包含 ArkTS、Native C++、CMake、HDC 的 HarmonyOS SDK。工程产品目标为 **HarmonyOS 5.0.0 / API 12**，运行设备需要 HarmonyOS NEXT（HarmonyOS 5 或更新）和 H.264 硬件解码能力；只配置 `arm64-v8a`。如果新版本 IDE 提议迁移 Hvigor / SDK 产品版本，请通过 IDE 完成迁移并重新编译，不能把这里的纯协议测试当作 SDK 兼容证明。
2. 在 DevEco Studio 打开本目录，等待 Hvigor / ohpm 同步。使用 Project Structure → Signing Configs 为 `com.tablink.harmony` 配置自己的开发签名和获授权的测试设备。本仓库不包含证书、私钥或签名描述文件，也不自动申请账号权限。
3. Build → Build Hap(s) / Make Module，处理完整 ArkTS 静态检查、NAPI 头文件及链接结果，再用 IDE Run 部署到真机。未完成此步骤前，不应发布 HAP 或声称客户端已经能够正常使用。
4. 在电脑 TabLink 创建一个独立的原生设备会话。电脑与设备位于同一 Wi-Fi；或者用户已建立可用的 USB 网络共享/IP 通道。这里不依赖 ADB，也不自行切换 USB 模式；USB 网络能力取决于手机/平板系统是否提供。
5. 将该会话的完整连接信息粘贴到鸿蒙 App，点击连接。每台设备用不同会话信息。停止所有会话后，电脑才可添加设备所需的分辨率/刷新率模式。

本机能够运行的检查只有：

```powershell
node native/harmony/tests/protocol.test.mjs
node native/harmony/tests/update-policy.test.mjs
node native/harmony/tests/project-check.mjs
```

这分别检查纯 TypeScript 配对/分包/PTS 逻辑、与 Windows/Android 共用签名 fixture 的 P-256 互操作及严格更新策略、工程资源引用与关键协议约束；都不会连接电脑服务、改变显示器、打开 AppGallery 或执行 Harmony SDK 构建。

2026-09-29 复核结果：**84 项实际纯 TypeScript 协议断言、43 项 stable 更新策略/签名断言、42 项工程/资源/关键源码路径静态检查通过**。更新断言用固定 P-256 SPKI 验证跨端 signed-envelope fixture，并覆盖篡改、严格版本/构建号、0–100 灰度边界和 bucket 20 固定向量、重复平台/JSON 字段、整秒 UTC 与小数秒拒绝、带 query 的正式下载 URL、userinfo/fragment 拒绝、应用市场/正式跳转严格 allowlist、时间及 cohort 策略。ArkTS SDK 类型检查、C++ 编译链接、Crypto Architecture Kit 真机验签和 AppGallery 行为仍未验证，详见 `VERIFICATION.md`。

## 文件结构

| 路径 | 职责 |
| --- | --- |
| `entry/src/main/ets/pages/Index.ets` | ArkUI 连接、Surface 生命周期、停止与旋转重连 |
| `entry/src/main/ets/protocol/Session.ets` | TLS 证书固定、认证、配置/视频接收、0x14 统计 |
| `entry/src/main/ets/protocol/Wire.ts` | 有界二进制分包与严格连接信息解析 |
| `entry/src/main/ets/protocol/DisplayProfile.ets` | 读取当前物理尺寸、方向和刷新率 |
| `entry/src/main/ets/update/StableUpdatePolicy.ts` | 严格 signed-envelope / stable SemVer / cohort / artifact 策略 |
| `entry/src/main/ets/update/StableUpdateCrypto.ets` | 固定 P-256 SPKI 验签和 cohort SHA-256 的 SDK 隔离层 |
| `entry/src/main/ets/update/StableUpdateManager.ets` | 启动、前台和周期检查；只把签名市场链接交给系统 |
| `entry/src/main/cpp/Decoder.cpp` | NAPI、硬件 AVCodec、NativeWindow、缓冲区生命周期 |
| `PROTOCOL.md` | 与 Windows 原生服务互通的数据合同 |

## 尚待实际完成的验证

- 用真实 HarmonyOS SDK 进行 ArkTS/NAPI/CMake 编译和签名。本项目尚未得到此证据，可能需要随指定 SDK 修正构建配置或 API 类型差异。
- 在至少一台 HarmonyOS NEXT 手机/平板上验证 Surface 生命周期、H.264 SPS/PPS/PTS、硬解能力、清晰度和稳定运行；若硬解器不支持实际原生分辨率，应在 UI 显示错误，不能谎报已经播放。
- 用实际设备测试旋转、后台/前台、网络断开、主机停止、错误证书拒绝、第二/第三设备并行和独立回收。
- API 12 的 display 公开属性提供当前刷新率，本客户端只报告观测到的模式，不伪造完整硬件模式列表。高刷新率支持与实际提交/呈现需要另行测量。
- 用 API 12 Crypto Architecture Kit 验证固定 X.509 SPKI 和 DER ECDSA fixture，并实测官方清单请求、稳定 cohort 持久化、无 `harmony` artifact、灰度未命中、市场链接打开以及应用市场自动更新设置；当前只有纯 Node/源代码证据。
- 触控回传、音频和扫码识别不在这个接收端源实现中；连接通过粘贴信息，画面通过真实原生解码路径。

## 核对过的官方资料

- [OpenHarmony：TLS socket 接口](https://github.com/openharmony/docs/blob/master/en/application-dev/reference/apis-network-kit/js-apis-socket.md) — `TLSSocket`、`getRemoteCertificate`、API 12 `skipRemoteValidation`。这里仅为自签证书握手跳过系统 CA 验证，随后强制比对显式证书指纹；没有无条件信任远端。
- [OpenHarmony：视频解码开发指南](https://github.com/openharmony/docs/blob/master/en/application-dev/media/avcodec/video-decoding.md) — 硬件能力选择、Surface 解码、异步输入/输出、动态库。
- [OpenHarmony：原生视频解码 API](https://github.com/openharmony/docs/blob/master/en/application-dev/reference/apis-avcodec-kit/capi-native-avcodec-videodecoder-h.md) — `RegisterCallback` / `RenderOutputBuffer`。
- [OpenHarmony：AVBuffer 标记](https://github.com/openharmony/docs/blob/master/en/application-dev/reference/apis-avcodec-kit/capi-native-avbuffer-info-h.md) — 使用 API 枚举中的 `AVCODEC_BUFFER_FLAGS_*` 名称。
- [OpenHarmony：XComponent](https://github.com/openharmony/docs/blob/master/en/application-dev/reference/apis-arkui/arkui-ts/ts-basic-components-xcomponent.md) — API 12 `XComponentOptions`、`onSurfaceCreated` / `onSurfaceDestroyed`。
- [OpenHarmony：display](https://github.com/openharmony/docs/blob/master/en/application-dev/reference/apis-arkui/js-apis-display.md) — 物理像素尺寸、方向、当前刷新率。
- [华为：视频解码播放远程视频](https://developer.huawei.com/consumer/en/doc/harmonyos-guides/video-decoding-play-remote) — 原生 Surface 视频播放流程。
- [华为：指定二进制数据转换非对称密钥对](https://developer.huawei.com/consumer/cn/doc/harmonyos-guides/crypto-convert-binary-data-to-asym-key-pair) — X.509 DER 公钥与 `ECC256` `convertKey`。
- [华为：使用 ECDSA 密钥对签名验签](https://developer.huawei.com/consumer/cn/doc/harmonyos-guides/crypto-ecdsa-sign-sig-verify) — `ECC256|SHA256`、`Verify.init/update/verify`。本机未用 SDK 编译或真机验证其 DER 签名互操作。

资料核对日期：2026-09-29。代码为本项目编写，未复制第三方样例工程；没有附带非公开 SDK。
