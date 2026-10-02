# TabLink 0.8.2 Preview 2：解码器自动切换与同会话恢复

TabLink 0.8.2 Preview 2 是 Windows 和 Android 的预览候选版。本版继续只允许一台接收设备占用一块 TabLink 虚拟副屏，并在 Preview 1 的六阶段连接健康中心基础上，加入 Android H.264 decoder 评分、自动切换和同一连接内的关键帧恢复。单个 MediaCodec 失败且存在可成功启动的备用候选时，客户端会在同一连接和副屏内切换；全部候选耗尽时仍按连接故障处理。

作者：**张林杰（Jey / [@linjierd](https://github.com/linjierd)）**；博客：[Linjie / 开发笔记](https://linjie.space/)

## 版本与分发范围

- Windows 主程序版本为 `0.8.2`。
- Android 公开预览 APK 为 `0.8.2`、`versionCode 14`。
- 原生传输协议主版本仍为 v1，并提供协商式 `render-submitted-v1` 与 `decoder-refresh-v1` 可选能力。
- Apple 与 HarmonyOS NEXT 工程本轮仍为 `0.8.0`。
- 公网稳定自动更新频道仍保持 `0.8.0`。本预览不会生成或发布新的 stable 清单。
- GitHub 公共 Windows 包仍不分发 Google Platform-Tools，也不包含有地域分发限制的 SIPSorcery 浏览器接收组件。Windows x64 ZIP 为 self-contained。
- 完整 Windows 目录必须整体交付；按需驱动依赖 `TabLink.DriverSetup.exe`、签名驱动文件和配套配置。

## 六阶段连接健康中心

“连接记录”页按顺序呈现：

1. 线路与监听；
2. 认证与屏幕参数；
3. 唯一虚拟副屏；
4. 捕获、编码与发送；
5. 客户端解码提交；
6. 客户端呈现回调。

状态分为等待、进行中、正常、暂停和需处理。新的连接 attempt 会隔离上次连接的迟到回调；乱序事件不能让阶段倒退或把新连接错误地标为正常。安全桌面等已确认暂停会保留连接和副屏所有权，恢复后再继续判断新帧。

“安全修复”只在阶段进入“需处理”时启用。它按故障阶段执行有限动作：刷新线路、重建连接、配置请求模式、重试回收本次拥有的副屏、重启视频，或打开日志。显示模式修复会先停止所有 TabLink 会话；精确回收只使用当前租约和受保护所有权回执，不按设备友好名称批量删除。

## 解码提交与呈现回调分离

Android 0.8.2 与当前 HarmonyOS NEXT 源码都在 `0x10` HELLO 中声明 `render-submitted-v1`。Windows 只回显双方都支持的能力；客户端只有看到回显后才发送 `0x14`。旧客户端不声明能力时继续使用既有协议，未知能力不会被回显，任何未协商客户端发送 `0x14` 都会作为协议错误结束当前会话。设备自报的平台或证据类型不能绕过协商。

`0x14 render-submitted` 表示 H.264 访问单元已成功排入当前 MediaCodec 输入队列。它带每个 TCP 会话累计帧数、本帧 PTS、尺寸、受限长度的 decoder 名称和提交 FPS。第一次提交立即报告，后续最多约每秒一次；解码器重配允许媒体 PTS 重新从零开始，但会话累计帧数不回退。

`0x12 frame-presented` 仍是独立的客户端呈现回调证据。Windows 分别保存 submitted 和 presented 的计数、FPS 与新鲜度；`0x14` 不会增加 presented 计数，也不能把“客户端呈现回调”阶段标为正常。该回调比解码提交更接近 Surface 输出，但仍不等于 SurfaceFlinger 或物理面板逐帧测量；超过新鲜度窗口的旧 FPS 不会继续冒充当前速度。

## Decoder 评分、切换与恢复

Android 不再只选择枚举到的第一个硬件 AVC decoder。客户端会核对目标尺寸与帧率，结合硬件加速、Android PerformancePoint、低延迟能力、软件实现和本进程内的失败记录排序候选。尺寸不支持的实现会排除，其他硬解优先，软件 decoder 保留为最后兜底；失败记录不会写成永久设备黑名单。

候选只有在 `MediaCodec.configure()` 和 `start()` 都成功后才会被公布为实际选中。初始化或运行阶段失败时，恢复任务会在 codec callback 返回后执行；旧 codec 实例和单调 generation 会共同过滤迟到 callback。切换会清空旧的压缩依赖链并等待新 IDR，避免将旧 decoder 的 P 帧送入新 decoder。

备用 decoder 启动成功后，Android 只有在电脑明确回显 `decoder-refresh-v1` 时才发送 `0x15`。载荷是 8 字节大端正 generation，不包含 codec 名、设备标识或异常文本。Windows 为当前已认证连接设置局部门控：保留编码器、TLS/TCP、显示租约和虚拟副屏，暂停该客户端的非 IDR 帧；下一枚自然 IDR 会在同一个 `0x21` 中补齐缺失的 SPS/PPS 后发送。不会重发 `0x20`，因此不会二次重建 decoder，也不会形成刷新循环。

`0x15` 不刷新 submitted 或 presented 健康期限。同一 generation 幂等，较高 generation 可合并，并有连接级限频。Android HUD 将面板 Hz、请求 Hz、解码提交 FPS 与呈现回调 FPS 分开显示；切换后的等待状态只能由备用 decoder 的真实呈现回调结束。旧电脑端不回显新能力时，新客户端不会发送未知消息，只等待编码器原有的自然 IDR。

## 单副屏与按需驱动边界

USB 调试、Wi-Fi / USB 网络、原生配对页和浏览器接入共用一个全局显示租约。任一连接已经占用或正在准备 TabLink 副屏时，第二个显示请求会在驱动操作前失败。

应用启动、监听网络、生成二维码和等待认证不会安装虚拟显示设备。接收设备完成授权并提交有效屏幕参数后，电脑端才检查并按需创建唯一的、所有权可证明的 `Root\MttVDD` 设备。正常断开会先停止视频和输入，再收回显示目标并移除该活动设备；Driver Store 中的签名驱动包与单输出模式配置继续保留。

随包驱动保留上游固定哈希和 SignPath 通用 Authenticode 签名；发布验证机的 `/pa` 检查通过，catalog 覆盖精确 INF 与 DLL。该签名不是 Microsoft WHQL 或 attestation 签名，验证机的 `/kp` 内核策略检查未接受其证书链。是否允许安装由接收电脑的 Windows 驱动信任策略决定；TabLink 不会安装证书、启用测试签名、关闭安全启动或降低系统策略。0.8.2 的独立真机驱动安装状态见验证记录。

## 兼容性

- 旧 Android 客户端没有 `features` 字段时仍可连接并使用 `0x12`。
- Android 0.8.2 连接旧电脑端时不会发送未协商的 `0x14` 或 `0x15`。
- HarmonyOS 源码会声明 `render-submitted-v1`；该工程尚未经过 Harmony SDK 构建、签名或真机验收，也没有已发布 HAP。
- 显示位置变化仍可在同一副屏身份下恢复；主副屏关系、镜像模式或目标身份变化会停止采集。
- 本轮没有加入多副屏、自适应码率、Intel QSV / AMD AMF、音频、压感笔或多点触控。

## 验证边界

构建、自动化测试、APK 签名、制品哈希和真机状态分别记录在 `VERIFICATION-0.8.2.md`。历史 0.8.1 生命周期结果只能作为回归背景，不能替代 0.8.2 的独立验证。
