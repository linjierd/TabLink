# Harmony 原生工程复核记录

日期：2026-10-02。主机：Windows，无可用 Harmony SDK / DevEco Studio。

实际执行：

```powershell
node native/harmony/tests/protocol.test.mjs
node native/harmony/tests/update-policy.test.mjs
node native/harmony/tests/project-check.mjs
```

- 84 项协议断言通过。测试执行仓库 `Wire.ts` 经 Node 内置类型剥离后的实际函数，覆盖全部原生端口、非法 URI/指纹、分片/合包、2 MiB 大帧分片、包长/PTS/Annex-B/SPS/PPS 与新解码器首次输出门槛。
- 64 项 stable 更新策略/签名断言通过。Node 使用内置 P-256 验签固定跨端 signed-envelope fixture，并验证篡改拒绝、三种持久模式的默认/损坏设置策略、串行模式写入及失败后继续、博客与 GitHub 地址、最新发布时间选择、URL-only/顺序镜像等价、同时间语义冲突拒绝及持久 blocked floor、暂停清单权威、完整规范决定 SHA-256 防回退及各语义字段覆盖、旧 floor 迁移门禁、未来协议权威状态、stable SemVer/构建号、0–100 灰度边界和 bucket 20 固定向量、平台唯一、整秒 UTC 与小数秒拒绝、带 query 的正式 HTTPS 下载 URL、userinfo/fragment 拒绝、应用市场/正式跳转严格 allowlist、cohort 与 Harmony artifact 选择。
- 85 项工程/资源/关键源码路径静态检查通过。它检查 JSON、`0.8.0` / code 800、页面引用、base/en_US/zh_CN 与 AppScope 资源键完全一致且非空、英文/简中格式占位符一致、跟随系统/简中/English 三种持久语言选择、连接与更新状态的资源键覆盖、证书指纹验证位于 token 发送之前、HELLO 声明 `render-submitted-v1` 且只在电脑回显后发送 0x14、原生硬解入口、RenderOutputBuffer 成功后才计数、不发 0x12、三个更新选项、模式写入队列与过期 UI 完成保护、双清单地址、先验签后解析、持久冲突阻断、完整决定摘要 floor、未来协议独立状态、无自安装 HAP，以及停止 worker 后才销毁 codec。
- 已对照官方 Socket 文档复核 `TLSSocket.send(ArrayBuffer)`、`getRemoteCertificate`、TLS 协议数组与 API 12 `skipRemoteValidation`。使用显式证书指纹校验后才发送应用层凭证；没有跳过 pin 的连接路径。
- 已人工审查 C++ 待处理 PTS、工作线程/回调资源顺序和输出尺寸校验。未用仿造 SDK 头文件产生“编译通过”证据。

此次收尾修正：原生端口白名单；对收到的大帧做有界增量填充；严格参数集/访问单元校验；重配后等待新输出，避免 `ptsUs=-1` 的错误进度；原生实际输出尺寸必须匹配会话；Ability 保持同步 `void` 生命周期入口；电脑采集暂停时不生成假进度。

语言选择保存在 Preferences 文件 `ui-language` 的 `app-language-v1` 键；缺失/损坏值使用 `system`，显式值为 `zhHans` 或 `english`。系统模式读取 `getSystemLanguage()`，所有 `zh-*` 显式映射到与 `zh_CN` 资源匹配的 `zh-CN`，其他语言映射到 `en`；静态检查已确认该分支和所有语言资源 key 完全一致，但没有验证 Localization Kit API 12 编译、资源热切换、系统语言改变、重启持久化或布局。

**没有执行 Harmony SDK / ArkTS / Native C++ 编译、签名、HAP 打包、设备安装、Localization Kit / Crypto Architecture Kit / AppGallery 运行或真机网络/显示测试。没有可交付的 HAP。** 现有 Node 断言不验证 ArkTS 的所有语法限制、固定 SPKI / DER ECDSA 的 SDK 互操作、Network Kit 对 GitHub `releases/latest/download` HTTPS 重定向的行为、Preferences 真机持久化、`setAppPreferredLanguage` 运行时资源刷新、NAPI ABI、NativeWindow / Surface 生命周期或实际硬件解码成功；这些必须在官方 IDE 和设备上完成。

限制：只实现原生 H.264 Surface 接收与粘贴配对；不支持 JPEG 测试包、原生扫码、触控回传、音频。网络故障后需要重新粘贴/连接；旋转重连会在内存中短暂保留当前配对。当前显示尺寸若为奇数会明确拒绝，不静默上报不同的屏幕参数。0x14 仅表示解码器已成功提交 Surface，不证明屏幕实际呈现。
