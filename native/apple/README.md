# TabLink 原生 iOS / iPadOS 客户端源码

作者：**张林杰（Jey / [@linjierd](https://github.com/linjierd)）** · 博客：[Linjie / 开发笔记](https://linjie.space/)

这是使用 **UIKit、Network.framework、VideoToolbox 和 Metal** 的原生客户端，不包含 WebView 或网页播放器。工程版本为 `0.8.0`、Apple 平台 build `2`，最低系统为 **iOS / iPadOS 17.0**。当前交付是可供 Mac 构建的完整源工程，**尚未经过 Swift 编译、Xcode 构建、签名、App Store 跳转或 Apple 真机验证，没有 IPA**。

## 打开与构建

在安装 Xcode 15 或更高版本、对应 iOS SDK 的 Mac 上，进入本目录：

```sh
open TabLink.xcodeproj
swift test
xcodebuild -project TabLink.xcodeproj -scheme TabLink \
  -configuration Debug -sdk iphonesimulator \
  -destination 'generic/platform=iOS Simulator' \
  -derivedDataPath DerivedData CODE_SIGNING_ALLOWED=NO build
```

`swift test` 执行 Foundation 协议测试；应用工程直接编入同一份协议源文件。Simulator 构建可检查 SDK / Swift / Metal 代码，但模拟器不证明硬解可用，客户端会拒绝未确认硬件解码的 H.264 会话。

真机安装时，在 Xcode 的 Signing & Capabilities 中选择自己的 Team，必要时修改 bundle identifier，然后选择已连接的 iPad / iPhone 运行。工程未包含任何签名身份、私钥或 provisioning profile。尚未制作 App Store 图标或分发归档，不把该工程视为已经可发布的 App Store 应用。

`TabLink.xcodeproj` 已提交，可直接打开；可选重建命令为 `python3 tools/generate_project.py`。没有 CocoaPods、SPM 外部包或网络下载步骤。

## 连接操作

1. Windows TabLink 启动“原生网络”会话，显示二维码。
2. iPad 与电脑使用可达的同一局域网。打开应用，选择“扫描电脑二维码”或“粘贴连接链接”，按系统提示允许本地网络 / 相机访问。
3. 首个画面实际显示后连接面板自动隐藏。右上角“连接”可重新打开面板、切换配对或断开；“重新连接本次配对”只记住本次应用运行中的配对。
4. 旋转设备会发送更新后的显示参数；服务器关闭 TCP 重新配置时，客户端使用同一 token 和证书指纹重连。电脑停止会话、凭证过期或更换证书后应重新扫码。

同时支持系统打开 `tablink://connect?...` URL。配对链接不会写入日志、UserDefaults、剪贴板或持久文件。应用仅在用户点击“粘贴”后读取剪贴板。没有相机时可粘贴链接。

**USB 在这里仅指已有可达 IP 网络上的相同 TLS 连接。** 本项目不提供 Lightning / USB accessory 传输，不使用 ADB，也不声称所有 iPad 都支持 Android 式 USB 网络共享。iPad 型号、电脑驱动及网络共享方式须另行实测；Wi-Fi 是本原生协议的直接连接方式。

## stable 正式版更新

应用启动、回到前台以及应用内定时器会检查固定的 HTTPS 清单地址：

```text
https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json
```

外层只能是 `{payload,signature}`。`payload` 是原始 UTF-8 JSON 的标准 Base64，`signature` 是 P-256 / SHA-256 的 ASN.1 DER ECDSA 签名。CryptoKit 使用内置的 X.509 SPKI 公钥验证精确 payload 字节；签名不通过、字段重复、未知字段、非 `stable`、预发行 SemVer、非整秒 `yyyy-MM-ddTHH:mm:ssZ` 发布时间、平台重复、非 HTTPS 地址、越界大小等情况全部失败关闭。签名通过后才解析 artifact。设备会在 UserDefaults 中保存随机 cohort，按 `SHA-256(cohort + "\n" + releaseId)` 应用 0–100 灰度比例；`0` 是有效的暂停发布值且不会选择更新。

清单没有 `ios` artifact 时，连接面板安静显示“尚未发布”；存在版本号和构建号都较新的正式版时，只接受同一签名负载中的 `apps.apple.com` URL，或正式 `https://linjie.space/download/api/download?path=TabLink%2Fstable%2F...` 跳转 URL。正式跳转必须是精确路径、恰好一个 `path` 参数、没有额外或重复参数。用户点击后由 `UIApplication.open` 前往 App Store。客户端**不下载 IPA、不侧载、不自行替换 App**；下载安装及以后自动更新由 App Store 和用户的系统自动更新设置负责。投屏期间不弹模态更新对话框，也不因检查或发现新版本中断连接；状态和按钮只在连接面板或用户主动打开连接设置时显示。

## 与 Windows 的协议契约

配对 URI 的结构为：

```text
tablink://connect?host=<IPv4>&port=<native-port>&token=<64 lowercase hex>&cert=<64 hex SHA256 DER>
```

只允许原生端口 `27184, 27186, 27187, 27188, 27189, 27190, 27191, 27192`，明确拒绝浏览器端口 `27185` 以及其他端口。地址必须是规范的单播 IPv4；拒绝主机名、IPv6、环回、0/8、多播、重复 / 未知参数、fragment、额外路径和错误长度的凭证。

- `NWConnection` 使用 TLS 1.2 / 1.3。校验服务器叶证书 DER 的 SHA-256 与二维码指纹完全一致，再将该精确叶证书作为此连接的信任锚进行 Basic X.509 校验。不会以公共 CA 或仅域名匹配替代指纹，也没有非 TLS 路径。
- 包头为 `type:uint8 + payloadLength:uint32 big endian`，非零载荷上限 8 MiB；按明确长度接收，协议错误终止该会话。
- 连接后先发 `0x10 {protocol:1,token}`，再发 `0x13` 本机 profile；profile 写入完成后开始读视频。
- `0x20` 解析 `{codec:"video/avc",width,height,fps,csd0,csd1}`。两个 base64 参数集必须分别包含 Annex-B SPS / PPS；尺寸 / 帧率 / 参数长度均有限制。实际 SPS 格式尺寸必须与声明相符。
- `0x21` 包含非负 8-byte 大端 `ptsUs` 与 Annex-B access unit。转换为 AVCC 长度前缀，构建 CoreMedia sample，交给 VideoToolbox 强制硬件解码；创建后再次读取硬件解码属性确认。未确认硬件解码会明确停止，不假报硬解。
- `0x01` JPEG 测试 / 回退画面使用 ImageIO 解码为 BGRA，同样经过 Metal 呈现。`0x02` 状态和 `capturePaused` 在 UI 中显示；`0x03` 结束会话并提示重扫，不回显不受信的原始错误文本。
- `0x11` 单指触控映射鼠标 down / move / up，按实际 contain 图像区域归一化，黑边不触发点击；move 最快 60 次 / 秒，取消 / 旋转 / 断开时释放按下状态。当前没有键盘、音频、手势滚动或多点触控。
- `0x12` 只在 `CAMetalDrawable.addPresentedHandler` 提供正的 `presentedTime` 后产生。每 TCP 会话按实际呈现的新帧计数，约每秒报告一次 `kind,sequence,width,height,fps,codec,decoder`；首次实际呈现立即报告。重复 drawable、旧会话、零时间和乱序帧不推进计数。解码成功、命令提交、GPU completion 或黑色清屏都不计作显示。

profile 包含当前像素宽高、rotation、nativeWidth / nativeHeight、刷新率和 supportedModes。H.264 编码尺寸按偶数向下取整，另用 physicalNativeWidth / physicalNativeHeight 保留未经调整的屏幕尺寸。请求刷新率取设备公布的 maximumFramesPerSecond，不承诺系统实际以该刷新率运行；FPS 证据来自呈现回调。

## 生命周期与延迟控制

网络接收和硬解不阻塞主线程。硬解至多 4 个在途输入、Metal 至多 3 个在途视频命令，渲染器只保留最新已解码画面，保留 H.264 压缩参考帧完整性。同步丢帧、异步回调和错误通过同一待处理登记表释放解码配额，避免重复释放和没有回调时的 context 泄漏。

网络中断按 1 / 2 / 4 / 8 / 10 秒退避重连；超过 15 秒没有接收活动会重建连接。超过 5 秒没有新的实际呈现会恢复连接面板。进入后台释放连接、相机、解码器和图像缓存；回到前台由用户点重新连接。会话代次检查阻止旧解码 / 呈现回调更新新会话。应用只在会话工作期间保持屏幕常亮。

## 验证范围

本次 Windows 环境仅执行：

```powershell
python tools/generate_project.py
python tools/verify_source.py
```

后者检查工程源文件引用、PBX ID 完整性、plist / scheme、生成可重复性、独立 Python 参考解析器处理的固定协议 fixtures，以及更新公钥、正式地址、版本元数据和 Apple/Android 共用 signed-envelope fixture。**这些检查不执行 Swift，不等于 XCTest 通过、CryptoKit 真机通过或 Xcode 编译通过。** Swift XCTest 除连接协议和真实呈现计数外，还覆盖固定 P-256 清单验签、payload 篡改拒绝、stable SemVer、重复 JSON 字段和 cohort 策略。

Mac 上必须首先完成上述 `swift test` 和 `xcodebuild`，再做真机联调：错误指纹拒绝、过期二维码拒绝、H.264 硬解属性确认、JPEG 测试画面、上下方向 / 色彩 / 黑边 / 触控校验、旋转重连、拔网 / 锁屏 / 进入后台、多个原生端口，以及主机的实际显示 ACK 增长。更新还必须验证固定 SPKI 和 DER ECDSA fixture、无 `ios` artifact、灰度未命中、篡改拒绝、App Store 链接打开与系统自动更新设置。长时间内存、温度、耗电、实际 FPS 与多型号兼容性均未验证。

## 官方 API 依据

- [Network.framework NWConnection](https://developer.apple.com/documentation/network/nwconnection) 与 [TLS 自定义验证回调](https://developer.apple.com/documentation/security/sec_protocol_options_set_verify_block(_:_:_:))：TCP / TLS 建连与对端证书验证。
- [VideoToolbox 硬件解码要求](https://developer.apple.com/documentation/videotoolbox/kvtvideodecoderspecification_requirehardwareacceleratedvideodecoder) 和 [确认正在使用硬件解码器](https://developer.apple.com/documentation/videotoolbox/kvtdecompressionpropertykey_usinghardwareacceleratedvideodecoder)：这两个键在 iOS / iPadOS 17 起可用，因此工程最低版本是 17。
- [VTDecompressionSessionDecodeFrame](https://developer.apple.com/documentation/videotoolbox/vtdecompressionsessiondecodeframe(_:samplebuffer:flags:framerefcon:infoflagsout:))：异步输出及同步 frameDropped 标志，用于配额 / 生命周期处理。
- [MTLDrawable.addPresentedHandler](https://developer.apple.com/documentation/metal/mtldrawable/addpresentedhandler(_:))：实际 presented 回调作为显示证据；没有把 enqueue 或 GPU completion 当作显示。
- [AVCaptureMetadataOutput](https://developer.apple.com/documentation/avfoundation/avcapturemetadataoutput)：本机相机二维码识别，无外部扫码服务。
- [CryptoKit P256.Signing.PublicKey](https://developer.apple.com/documentation/cryptokit/p256/signing/publickey)：从 DER 表示创建固定公钥并验证 P-256 签名。
- [UIApplication.open](https://developer.apple.com/documentation/uikit/uiapplication/open(_:options:completionhandler:))：把签名清单中的 App Store 链接交给系统处理；App 内不自替换。

只使用平台自带框架；没有复制第三方二进制、示例库或 SDK，平台框架由 Apple SDK / 系统提供。
