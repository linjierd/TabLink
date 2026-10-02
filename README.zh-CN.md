# TabLink · 单设备独立副屏

[English (Singapore)](README.md) · **简体中文**

<!-- tablink-version-contract: version=0.8.9; channel=preview; preview=1; androidVersionCode=21 -->

[![CI](https://github.com/linjierd/TabLink/actions/workflows/ci.yml/badge.svg)](https://github.com/linjierd/TabLink/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/TabLink%20code-MIT-blue.svg)](LICENSE)

GitHub 已发布 **0.8.9 Preview 1**，Android 身份为 **0.8.9 / build 21**。它在 0.8.8 的可信设备协议和单副屏生命周期之上，增加稳定线路的有界恢复、迁移中一次性登记 token 的剩余期限保护、用户主动导出的脱敏支持包、人工审核的兼容性目录，以及统一的跨平台版本门禁。协议主版本仍为 v1，全局仍只允许一块 TabLink 副屏。发布 tag `v0.8.9-preview.1` 精确指向提交 `2a1c3aced048315e3171489fc410c6adeb2eed66`；见 [0.8.9 发布说明](RELEASE-0.8.9.zh-CN.md) 与 [0.8.9 验证记录](VERIFICATION-0.8.9.zh-CN.md)。

四项公共资产已从 GitHub Release 的公开 HTTPS 地址重新下载并逐项核对；Windows ZIP 解压后的 463 个文件与最终 PublicRelease 逐字节一致。已签名的公网稳定自动更新频道在发布后重新下载并验签，仍保持 **0.8.0**，不会仅因 GitHub 预览包而自动切换；0.8.0 的签名更新设计见 [0.8.0 发布说明](RELEASE-0.8.0.md)、[自动更新设计与发布说明](AUTO-UPDATE.md) 及 [0.8.0 验证记录](VERIFICATION-0.8.0.md)。

## 下载 0.8.9 Preview 1

这是预发行版本。请完整解压 Windows ZIP 后再运行 `TabLink.exe`，并把 APK 安装到接收端 Android 设备。每个下载文件都应使用 `SHA256SUMS.txt` 核对。

| 资产 | 大小 | SHA-256 |
| --- | ---: | --- |
| [Windows x64 完整包](https://github.com/linjierd/TabLink/releases/download/v0.8.9-preview.1/TabLink-Windows-x64-0.8.9-preview.1.zip) | 97,320,509 字节 | `4448CCA4151247F535139DBEC5A9D62331E9EB66E89491E0AEB47481D2726206` |
| [Android APK](https://github.com/linjierd/TabLink/releases/download/v0.8.9-preview.1/TabLink-Android-0.8.9-preview.1.apk) | 346,098 字节 | `34DB1B9F2FD808D8BA7958F1744AA8915677F93FA7DABD820A62C86823F07C7D` |
| [FFmpeg 7.0.2 对应源码](https://github.com/linjierd/TabLink/releases/download/v0.8.9-preview.1/TabLink-FFmpeg-7.0.2-corresponding-source.tar.gz) | 28,919,316 字节 | `FD7977F53EDD262D55C49F200EB5F54B1B12F5FFA547770380448708D75EA6F2` |
| [SHA256SUMS.txt](https://github.com/linjierd/TabLink/releases/download/v0.8.9-preview.1/SHA256SUMS.txt) | 326 字节 | `D120A47FA25634F6F8AC33071D5039FB4339A3AE9E6D4EAEC6D9EBAD00B07EA5` |

[完整 Release 页面](https://github.com/linjierd/TabLink/releases/tag/v0.8.9-preview.1)包含所有公开资产。四项资产均已从 GitHub Release 的公开 HTTPS 地址重新下载并核对；Windows ZIP 解压后的 463 个文件与最终 PublicRelease 逐字节一致。

稳定自动更新频道仍保持在 0.8.0。

以下保留既有功能说明和历史记录；旧版运行条件、ADB 外置说明及旧帧率结果以新版说明为准，不能作为 0.8.9 的验证结果。

TabLink 是 Windows + Android 扩展桌面应用。Windows 通过已签名的开源虚拟显示驱动提供独立桌面，发送 H.264 视频，Android 使用 MediaCodec 解码并回传显示进度与单指触控。支持同一局域网的 Wi-Fi、USB 网络共享和原有的 ADB USB 兼容通道。

APK 会读取平板的原生尺寸、当前方向、支持的刷新率和活动模式。电脑端据此匹配副屏，支持横竖屏重新匹配、短暂中断后的重连，以及停止连接时自动收回副屏。

> **项目愿望 / Project vision**
>
> 我想持续做一些免费、好用、真正解决实际问题的小软件。如果你有新想法、功能建议，或遇到希望用软件解决的问题，欢迎通过 [GitHub Issues](https://github.com/linjierd/TabLink/issues) 告诉我。
>
> My goal is to keep building small, free, useful tools that solve real problems. If you have an idea, a feature request, or a problem you'd like software to solve, feel free to open a [GitHub Issue](https://github.com/linjierd/TabLink/issues).

## 反馈与脱敏支持包

缺陷、性能问题、设备兼容性和功能建议请从 [GitHub Issue Forms](https://github.com/linjierd/TabLink/issues/new/choose) 选择对应入口；安全漏洞请使用 [私密 Security Advisory](https://github.com/linjierd/TabLink/security/advisories/new)，不要发布公开 Issue。

Windows 程序的“检测与日志”页提供“导出脱敏支持包”。保存前会展示 ZIP 中的全部文本内容；v1 只按固定字段清单生成版本、兼容性和连接健康摘要，不读取或复制原始日志。程序不会自动上传或附加支持包，只有用户主动保存、检查并在 Issue 中选择该文件时，文件才会离开本机。公开 Issue 仍不得包含序列号、USB/PnP ID、IP/MAC、路径、配对链接、token、证书、密钥或原始日志。

## 兼容性目录

[公开兼容性目录](compatibility/README.zh-CN.md) 按一次具体的 TabLink 版本、电脑/GPU、接收设备、连接方式和显示参数记录经过人工复核的历史观察。每一行只证明该行的精确配置和测试日期；没有记录不表示不支持，一个型号的一次成功也不代表它的所有系统版本、电脑和线路都可用。请求刷新率、解码提交帧率、呈现回调帧率和物理呈现帧率始终分开记录。

兼容性 Issue 和用户主动附加的脱敏支持包只是候选证据，不会自动写入目录。维护者只把公开、非唯一、可审查的字段手工整理进受控 JSON；验证工具拒绝未知字段和常见的序列号、USB/PnP ID、地址、路径、token、证书或密钥形式。自动检查只能约束数据形状并拦截常见泄露，不能代替人工确认公开型号和测试结论。目录不会被 Windows 或接收端下载，也不会改变运行时的编码器、解码器或设备选择。

## 作者

- **张林杰（Jey）** · GitHub：[@linjierd](https://github.com/linjierd)
- 博客：[Linjie / 开发笔记](https://linjie.space/)

作者与项目链接也记录在 [AUTHORS.zh-CN.md](AUTHORS.zh-CN.md) 中。

Windows 程序默认在底部显示同一作者、GitHub 与博客信息。在“设置 → 底部作者信息”中可以关闭或重新开启，也可以修改作者文字、链接显示文字和 HTTPS 地址；保存后立即生效，不会断开正在使用的副屏。该偏好只保存在 `%LOCALAPPDATA%\TabLink\author-footer.json`，与 USB 排除和授权策略分离，不进入脱敏支持包，也不会改变仓库中的正式作者或许可证信息。

## 开源范围与许可证

TabLink 自有源码采用 [MIT License](LICENSE)。公开仓库只跟踪源码、测试、补丁、依赖来源和许可证；本机诊断、设备标识、构建缓存、ADB/FFmpeg 下载文件、APK 和完整发行包不进入 Git 历史。发布二进制通过 GitHub Release 或项目下载服务提供，并应附带 SHA-256 与适用的第三方许可。

第三方组件仍受各自许可证约束，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。尤其是浏览器接收功能当前使用的 SIPSorcery 10.0.16 在 BSD-3-Clause 之外还有额外地域/用途限制，因此该依赖不是 OSI 批准的开源许可证；不能把包含它的整个二进制组合描述为不受限制的纯 MIT 软件。

## 当前验证状态

截至 2026-10-02 的开发验证已观察到：

- 0.8.4 的普通桌面发送约为 **65.09 fps**；0.8.5 候选修复后，Android 接收/提交/解码约为 **89.98 fps**，呈现回调约为 **89.95 fps**，该窗口所有输入、队列、调度与背压丢帧均为 0。
- 同一 0.8.5 候选的原生 D3D11 动态源测试中，Android 接收约 **90.04 fps**、解码与回调约 **90.01 fps**；SurfaceFlinger 的最终实际呈现约为 **87.98 fps**。面板 Hz、编码输出、解码回调和最终可见呈现始终分别报告。
- 中兴 W202DS 原生尺寸为 **1200 × 1920**，支持 **60 / 90 Hz**；横屏为 **1920 × 1200**。
- Windows 虚拟副屏已运行于 **1200 × 1920 @ 90 Hz**，物理主屏保持 **2560 × 1600 @ 240 Hz**。
- H.264 已通过 USB 到达平板，实际硬件解码器为 `c2.unisoc.avc.decoder`；修复电脑端采集等待精度后，解码回调约为 **90 帧/秒**。
- 已通过中兴的可见开发者显示选项“锁定刷新率”，让平板实际运行于 **90 Hz**。APK 与 SurfaceFlinger 均确认活动模式 90 Hz，物理周期为 11,111,111 ns。原设置备份位于 `diagnostics/android-display-settings-before.json`；需要恢复自适应时可关闭该显示选项。
- 面板刷新率与实际视频画面更新率是不同指标。同一 D3D11 动态源的电脑端采集测试已达到 **89.70 张不同画面/秒**，没有重复旧帧凑数。平板最终呈现与有界呈现调度的对照结果见 `VERIFICATION.md`。
- 最终 0.4.2 在电脑端最小化、相同动态源的 **120.433 秒连续测试**中，平板实际呈现 **89.702 fps**；四段 30 秒均为 89.57–89.80 fps，P99 间隔 11.147 ms。已修复运行中时间映射漂移造成的再次降帧，实机截图与完整原始证据在交付目录 `diagnostics/`。

完整测量条件与结果由单独的 `VERIFICATION.md` 记录。窗口负载、USB 和 Android 合成策略都会影响实际呈现，不以请求的 90 Hz 代替测量结果。

## 运行与使用

既有版本已在 Windows 11 x64、中兴 W202DS 平板和 NVIDIA RTX 4060 Laptop GPU 上完成过显示与性能验证；0.8.8 Preview 1 的历史验收记录在 [VERIFICATION-0.8.8.md](VERIFICATION-0.8.8.md)，0.8.9 Preview 1 的独立验收与公共发布闭环记录在 [0.8.9 中文验证记录](VERIFICATION-0.8.9.zh-CN.md)。GitHub Release 的 Windows x64 公共包为 self-contained，不需要另装 .NET；从源码运行或使用普通 framework-dependent 构建时需要 .NET 10 Desktop Runtime。0.8.8 起会在每次新连接开始前实际探测可用 H.264 后端；当前 NVIDIA 主机的 Auto 路径已选择 NVENC。QSV 已编入 helper，但本机没有可用的 Intel MFX 实现；AMF 已编入 helper，但本机没有 AMD AMF 运行库。两者都明确失败并保持强制后端不变，仍需在相应 Intel / AMD 电脑上做实机验证。APK 支持 Android 6.0 / API 23 及以上，实际解码能力和刷新率由设备决定。

### 正式版自动更新

Windows、Android、iPhone / iPad 与 HarmonyOS NEXT 原生客户端都以内置的个人博客地址作为 `stable` 清单主来源，以 GitHub 最新非 prerelease 正式 Release 的 `manifest.json` 作为备用来源：

```text
https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json
https://github.com/linjierd/TabLink/releases/latest/download/manifest.json
```

两个地址只负责传输，不代表自动可信。客户端都用同一枚内置 ECDSA P-256 公钥验签，并核对下载包的签名保护大小与 SHA-256；两份清单都有效时采用 `publishedAtUtc` 较新的签名决定，同一时间的发布内容冲突会失败关闭，较新的已签名暂停也不能被旧镜像绕过。冲突时间会跨后续检查和重启持久保存：只有发布时间严格更晚的有效签名决定才能解除阻断，该时间及更早的普通决定仍会被拒绝。无效签名、篡改包、降级和预发布版本都会失败关闭。Preview / prerelease 不会推进 GitHub 的正式更新路径。

Windows、Android、iPhone / iPad 与 HarmonyOS NEXT 原生客户端都会持久保存本机已经接受的最高发布时间和发布决定摘要。即使重启后暂时只能访问一个来源，也不会接受早于该高水位的旧签名清单；同一发布时间只有下载 URL 不同、其余版本与安全字段完全一致时才作为等价镜像。

截至 2026-10-02，博客主地址返回 HTTP 530，GitHub 备用地址因公开 Release 仍全部是 Preview 而返回 404。在任一来源发布可用的已签名 stable 清单之前，端到端自动更新不可用；客户端会失败关闭，不会拿 Preview 资产或未签名内容顶替 stable 清单。任一来源恢复后，另一个来源不可达不会阻止客户端验证并使用可用来源。

Windows 与 Android 的“设置”都有三个选项：

| 选项 | 检查与下载 | 安装 |
| --- | --- | --- |
| **自动更新**（`Automatic`） | 启动或回到前台时检查，之后定时检查；Windows / Android 空闲时自动下载并校验，Apple / HarmonyOS 准备已验签的商店版本信息 | Windows 在副屏会话结束后安装；Android 再次确认最新签名决定后直接发起系统安装流程，不增加应用内确认，但仍保留 Android 系统确认；商店平台由 App Store / AppGallery 与系统设置管理下载和自动安装 |
| **自动下载后手动安装**（`DownloadThenAsk`） | 自动检查；Windows / Android 后台下载，Apple / HarmonyOS 自动验证商店更新信息但不声称预下载商店安装包 | 用户须明确点击安装或打开应用市场，Android 随后仍要求系统确认 |
| **从不更新**（`Never`） | 不发起后台更新请求，也不下载 | 不安装，并停用安装入口 |

首次没有偏好文件时默认为“自动更新”；偏好文件损坏时失败关闭为“从不更新”。四个原生平台的设置中都有这三项，切换后立即生效，不会断开正在使用的副屏。Windows 自动或手动受保护替换都只允许从精确的 `%ProgramFiles%\TabLink` 正式目录执行；E 盘、Desktop、OneDrive 或其他便携副本可以按策略检查和下载，但不能替换自身。独立更新器会核对清单签名、版本、包大小和 SHA-256，目录切换或新版启动健康检查失败时恢复上一版本。浏览器客户端随 Windows 主机资源一起更新。iPhone、iPad 与 HarmonyOS NEXT 原生客户端只接受同一签名清单中的 App Store / AppGallery 地址，由各平台应用市场负责下载和安装；在“自动下载后手动安装”下，它们会自动验证信息并等待用户明确打开应用市场。完整信任与发布流程见 [AUTO-UPDATE.md](AUTO-UPDATE.md)。更新缓存位于 `%LOCALAPPDATA%\TabLink\updates\`。

### Wi-Fi 或 USB 网络共享（无需开发者模式）

1. 在平板上安装完整交付包里的 `android/TabLink.apk`。可通过文件传输或浏览器下载后，用 Android 正常安装界面安装；无需 ADB 安装。
2. 选择连接方式。Wi-Fi：电脑与平板接入同一局域网；电脑也可以用有线网络接入同一路由器。USB：用数据线连接，在平板普通系统设置中打开 **USB 网络共享**。平板 TabLink 内提供该设置的入口。不需要打开开发者模式、USB 调试或无线调试。
3. 运行 `TabLink.exe` 并接受正常的管理员授权，进入 **Wi-Fi / USB 免调试**，点击 **刷新线路**。选择电脑 WLAN/有线网卡，或标有平板序列号的 **USB 网络**。不要为 USB 连接选择 WLAN，否则画面仍走 Wi-Fi。
4. 第一次使用时点击 **开始配对**，再在平板 TabLink 点击 **扫码连接**。相机不可用时可复制电脑端的连接链接，在平板粘贴。二维码中的随机 token 只授权一次设备登记，并在生成后 5 分钟失效；Android 同时提交 Keystore P-256 公钥，电脑在写入公钥和设备名称前即消费该 token，重复、过期或已经更换的二维码都会被拒绝。电脑已有可信设备并自动启动监听时不会显示二维码；需要添加设备时点击 **生成新配对二维码**。
5. 后续连接不再保存或重放二维码 token。平板先核对登记时固定的电脑 TLS 证书，再用 Keystore 私钥签署电脑发出的新挑战；签名验证和屏幕参数校验完成后，电脑才会恢复独立副屏，按平板的实际方向、原生分辨率与所支持的刷新率开始传输。
6. 电脑地址变化时，平板先尝试上次地址，再在当前局域网按已登记的电脑身份发现新地址。Wi-Fi 与 USB 网络共享可沿用同一份信任，不需要重新扫码。Windows 只会在同一物理线路的唯一新绑定（地址或接口绑定信息）连续确认至少 3 次且持续至少 8 秒后迁移监听；一次网卡枚举失败、候选歧义或仍有真实画面呈现都不会停止连接或卸载虚拟屏。如果原线路仍保持 Up 而另一条线路新出现，需要在电脑端先选择目标线路并重建监听。迁移启动失败后，后台只重试已经确认的线路，不会从下拉列表猜选别的网卡。发现结果本身不授予访问权限。电脑点 × 可在托盘继续工作；停止连接/退出会收回副屏。连接准备阶段的编码器目录读取与全部候选探测共用 30 秒总预算，首次有效呈现期限为 45 秒；首次呈现后，正常桌面连续 20 秒没有新呈现确认才会收回副屏。旋转平板会重新建立视频连接，信任关系不变。

Wi-Fi 和 USB 网络共享采用同一套 TLS 加密协议；应用在选择的本地 IPv4 上监听分配到的原生 TCP 端口，并在 UDP 27193 提供同网段发现。电脑证书和主机身份跨会话保持，二维码中的随机 256 位令牌只用于 5 分钟内的一次首次登记。发现请求包含协议版本、电脑身份和随机数，响应回显这些字段并增加当前端口；两者都不含 token、设备公钥列表或可直接连接的私密链接，响应还有同子网检查与速率限制。Android 在完整发现窗口内收集有界候选，再逐一核对固定证书；第一个假响应不能单独压住真实电脑。不会启用网络 ADB、发送 AOA 切换指令、修改默认路由/DNS，也不依赖云端中转。

电脑端“可信设备”列表可查看已登记设备的脱敏身份并撤销。撤销正在连接的设备会停止当前会话并收回副屏；以后来自该设备的签名也会被拒绝。Android 的“忘记上次配对的电脑”会删除本地公开信任元数据和对应 Keystore 私钥，之后需生成新二维码重新登记。

USB 网络共享本身可能向 Windows 提供上网网关和 DNS，因此系统可能把电脑其他流量改走平板；请按实际网络情况选择线路。TabLink 只绑定自己的视频连接，不擅自修改系统路由。Android 13 普通 APK 不能可靠地静默开启 USB 网络共享，因此仍需在系统设置中打开该开关；本版不承诺插线即连。

无线信号、路由器隔离、VPN/TUN 与设备负载会影响连通性和实际帧率；如无法连接，确认两端处于同一网段且路由器没有开启客户端隔离。请求 90 Hz 不等于实测解码或呈现达到 90 fps。当前可信配对与网络迁移的验证边界见 [0.8.9 验证记录](VERIFICATION-0.8.9.zh-CN.md)，不能用旧版 ADB 性能测试替代。

### USB 调试（兼容方式）

1. 退出 ExtensoDesk 的 USB 后台，避免它再次切换平板或 F50 Pro 的 USB 模式。
2. 用数据线连接平板，开启“开发者选项 → USB 调试”，首次连接在平板允许此电脑。
3. 打开完整交付目录中的 `TabLink.exe`，在 Windows 管理员授权窗口中选择“是”，然后点击“刷新设备”。0.5.1 起电脑端启动需要管理员权限；取消授权则不启动。ADB 兼容模式只接受 Google 官方 **SDK Platform-Tools r37.0.0 for Windows** 的 `adb.exe`、`AdbWinApi.dll`、`AdbWinUsbApi.dll` 固定哈希三件套；首次执行刷新、安装或连接等交互操作前选择完整 `platform-tools\adb.exe`。TabLink 核验三件套后复制到受保护的 `%ProgramData%\TabLink\Adb\sha256-<清单哈希>\`，后续管理员 ADB 操作不从用户路径、环境变量或 `PATH` 启动。
4. 选中平板，点击“安装安卓客户端”，将本次完整交付包的 `android/TabLink.apk` 安装到这台设备。程序先读取 Android 当前前台用户，再对同一用户使用保留数据的非流式覆盖安装；平板显示标准更新确认页时请完成确认。只有 ADB 返回明确成功结果后界面才会提示已安装，超时不会自动叠加第二个安装事务。
5. 点击连接后，程序在已确认目标设备及其屏幕参数后检查唯一的 TabLink 虚拟显示设备。设备不存在时，程序先把 TabLink 所有的驱动配置收敛为一个输出并写入所需模式，再安装虚拟显示设备；安装或核验失败时停止连接并说明原因，不会改用主屏或其他远程软件的虚拟屏。
6. 点击“连接选中的平板”。程序在会话开始时固定当前 Android 用户，屏幕参数读取、首次启动与断线恢复都只使用这个用户；若断线恢复时发现 Android 用户已经变化，本次会话会安全停止并要求重新连接。随后程序恢复独立副屏并建立视频连接。把窗口拖到主屏右侧的第二块桌面即可在平板使用。
7. 平板旋转后，程序结束旧视频会话并重新匹配方向，期间可能短暂显示连接状态。单指触控可移动、点击和拖动鼠标；可在电脑端取消“允许平板触控操作副屏”。
8. 点击“停止连接”会先结束视频与输入、释放鼠标键并收回本次显示租约，再移除本次连接所用的 TabLink 虚拟显示设备。随机 USB 转发只按受保护队列中的精确 `Owned` 收据清理；设备离线或 ADB 暂不可用时保留记录以后公平重试，但不延迟显示与驱动回收。签名驱动包和 TabLink 配置保留在电脑中，供下次连接快速重建；断开后 Windows 不应继续保留该虚拟屏。
9. 点击电脑窗口右上角 **×** 会隐藏到系统托盘，视频与连接监控继续运行。双击托盘图标或再次打开原来的快捷方式可唤回窗口。右键托盘图标可“停止连接”或“退出 TabLink”；“退出”才会清理连接并结束后台程序。

### 平板全屏与状态文字

客户端启动即进入全屏，无常驻控制栏。左上角分别显示面板 Hz / 请求 Hz、解码提交 fps / 呈现回调 fps，以及当前 decoder 和切换状态；缺失或超过 5 秒未更新的速度显示 `—`。文字默认白色，**透明度 30%（不透明度 70%）**。

长按状态文字，或使用 Android 返回手势 / 返回键，打开显示设置。可以选择九宫格位置、白/绿/青/黄/黑、自定义 `#RRGGBB` 颜色和 0–100% 透明度；更改即时保存。100% 透明时文字隐藏，仍可用返回手势打开设置。点击“完成”回到全屏，普通设置不结束 USB 视频会话。“重连”和“退出”收在这个面板内。

### Windows 管理员授权与画面暂停

Windows 的管理员授权、锁屏等操作可能使普通桌面暂时无法采集。0.5.0 会保留已认证的 USB 会话和副屏，向平板发送暂停状态与心跳；回到普通桌面后重建采集和解码状态。暂停期间保留最后画面，不把心跳计作新画面或解码帧率，也不向受保护桌面转发触控。

程序不显示或操作管理员授权界面的内容，不关闭 UAC，也不会为了恢复连接申请管理员权限。普通桌面上的持续采集故障仍有恢复期限；USB 拔出、设备身份变化、主进程退出或明确停止仍触发清理。

### 调整 Windows 显示器排列

在 Windows“系统 → 屏幕”中拖动副屏方块、修改左右上下位置后，程序会重新确认同一块独立虚拟副屏，更新采集位置与触控坐标。USB 会话保持连接，画面可能短暂停顿后恢复；无需先停止连接。程序会保存通过验证的新位置，在同一设备、其他显示器布局未变化且位置仍相邻不重叠时，供下次连接恢复。

这里只自动接受同一设备的位置改变。把副屏设为主屏、改成镜像、更换显示设备或手动改变尺寸/刷新率时，仍会停止不符合平板配置的采集。

短暂传输中断时 APK 会重新尝试认证。首帧准备最多 45 秒；已有真实呈现后，平板被拔下或普通运行期间约 20 秒未确认新画面时，电脑端会停止会话并收回副屏；再次插入后刷新并连接。已确认的桌面暂不可用期间暂停无帧回收，桌面恢复后提供一次 20 秒恢复窗口。主程序异常退出时，独立守护进程也会尝试收回它拥有的副屏。

## 按需驱动与单副屏生命周期

电脑端 `TabLink.exe` 从 0.5.1 起声明需要管理员权限：从普通桌面启动时，由 Windows 请求 UAC 授权，授权成功后程序及其副屏守护进程在管理员权限下运行。0.8.8 起不在程序启动、打开配对页、局域网发现或等待认证时安装虚拟显示设备；当前 0.8.9 仍只有在可信设备通过签名认证并提交有效屏幕参数、连接准备真正占用副屏时才执行检查与按需安装。

驱动使用 [VirtualDrivers / Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) 固定版本 25.7.23，附带原始签名二进制、MIT 许可、SHA-256 和来源记录。TabLink 安装前检查固定哈希与 Windows 通用 Authenticode 信任，不会主动启用测试签名、关闭安全启动、安装证书或降低签名策略。该上游签名不是 Microsoft WHQL 认证，实际安装仍受接收电脑的 Windows 驱动信任策略约束；拒绝时健康中心会保留错误，不会更改系统签名设置。驱动配置固定为一个输出；旧配置即使曾设置多个输出，也必须先收敛到一个再安装设备，避免连接瞬间重新生成多块虚拟屏。

全局显示租约上限同样固定为一个。USB 调试、Wi-Fi / USB 网络和浏览器接入共用该上限；已有副屏连接或正在准备连接时，第二个请求会在调用驱动管理组件之前被拒绝。要切换平板或手机，先停止当前连接，再连接下一台设备。

正常断开会移除确证属于 TabLink 的 `Root\MttVDD` 设备，但不删除 Windows Driver Store 中的签名驱动包，也不删除 TabLink 的模式配置。管理组件只操作通过所有权核验的设备；身份不明确、出现多个同类设备或清理失败时会停止并记录错误，不会猜测删除其他虚拟显卡。

## 设备排除与连接范围

- ADB 兼容模式只接受官方 Platform-Tools r37.0.0 固定哈希三件套。首次交互使用时将其复制到受保护 ProgramData staging；之后后台清理也只复核并复用该受保护副本。每次目标命令仍带确切 ADB 序列号，并重新检查 Windows USB 身份、ADB 授权和排除规则。无需 Root。Wi-Fi 与 USB 网络共享连接不使用 ADB，也不需要开发者模式。
- 新配置默认排除 F50 Pro 已知的 `19D2:0246`、`19D2:0621` 两种 USB 身份，没有排除整个中兴 VID。为了保护隐私，源码不内置任何真实设备序列号；请在“设置”页的“设备保护”区域把自己的随身 Wi-Fi 序列号加入本机设置，使它在切换 USB 产品身份后仍被阻止。
- 不发送 AOA 握手、不重置 USB、不启用网络 ADB，也不全局移除其他软件的转发。每个 ADB 会话只在 `--no-rebind` 明确成功后拥有指定设备的随机端点；已有占用会阻止创建，不会先删除或替换。清理前会第二次核对同一端点，但 ADB 不提供跨进程原子 compare-and-delete，最终核验后的外部管理员进程竞态仍是公开边界。
- `%ProgramData%\TabLink\UsbReverseCleanup\` 使用每收据一文件的 `Prepared` → `Owned` 两阶段队列并绑定原 Windows 用户 SID；持久递增的 sequence 负责公平轮转，不依赖文件 mtime。跨用户的 `Prepared` 因从未拥有删除权限，可在不接触 ADB 的情况下安全封存；跨用户的 `Owned` 会保留并轮转，当前用户不会对它执行 ADB。`%LOCALAPPDATA%` 的 0.8.6 或更早记录只用于诊断，不能授权管理员 ADB 删除；USB 队列异常也不能阻止精确回收 TabLink 的显示目标。
- ADB 兼容服务仅监听 `127.0.0.1:27183`；Wi-Fi / USB 网络服务监听手动选择的本地 IPv4 和原生端口，并在 UDP 27193 接受同子网、指定 host ID 的有限发现请求。首次登记使用一次性随机 token；以后固定持久电脑证书并验证 Android P-256 挑战签名。认证通过后才配置、发送副屏画面，无需外网中转。
- USB 网络网卡由 Windows 网卡 GUID、PnP 父设备链和真实 USB 序列号识别，复用同一份排除规则；不能按“中兴”厂商或“RNDIS”名称混选 F50 Pro。无法确认身份的 USB 网卡不列为可用线路。Wi-Fi 设备通过用户明确扫码授权，USB VID/PID 排除不被描述为网络身份认证。
- 显示目标同时核对 CCD、实际 `Root\MttVDD` 适配器、`MTT1337` 显示器及独立非主屏状态。ExtensoDesk、ToDesk、向日葵等其他虚拟显卡不会被选为 TabLink 目标。
- 排除配置位于 `%LOCALAPPDATA%\TabLink\settings.json`。配置损坏时拒绝连接，不自动清空规则。

## 视频链路与帧率

正常链路为：确证的唯一虚拟副屏 → 优先 Desktop Duplication / DDA 捕获 → 经运行时探测选定的 FFmpeg H.264 后端 → TLS 局域网 / TLS USB 网络 / ADB USB → Android MediaCodec → Surface。

DDA 目标按实际适配器、输出和显示边界严格核对，不简单选择“第 0 块屏幕”。当前实现优先 DDA；无法验证相应 DXGI 输出或首次启动失败时，可退回对同一确证副屏的 GDI 捕获，并在连接记录中说明。捕获方式变化不会改为抓取主屏。Auto 会真实初始化并测试 NVENC、QSV 或 AMF 候选；只有用户明确允许时才考虑独立的 libx264 软件 helper。强制指定的后端失败会明确停止，不静默回退。

### 编码器选择与软件回退

“连接副屏”页可以选择 Auto、NVIDIA NVENC、Intel QSV、AMD AMF 或软件 x264。Auto 优先与目标输出适配器厂商相符且通过目标尺寸/帧率探测的硬件后端，再按确定顺序尝试其他硬件后端。目录读取与全部候选共享一次 30 秒启动预算；未授权的软件 helper 不会被启动，软件 helper 损坏也不会阻止健康的强制硬件后端。选择一经成功就在本次连接中保持固定；安全桌面、DDA/GDI 恢复和自适应码率重建继续使用同一后端，新连接才重新探测。

软件 x264 默认禁用，只有勾选“允许软件回退”或明确选择 x264 时启用。其视频有效目标最高为 30 fps，并限制线程和进程优先级，以避免再次让电脑变卡；这不会降低平板的原生分辨率、请求刷新率或 Windows 虚拟显示模式。浏览器接收端会按实际 30 fps 媒体节奏计算 RTP 时间戳，同时保留设备请求的 60 Hz 虚拟显示模式。硬件 helper `ffmpeg.exe` 为 LGPL 配置，包含 NVENC / QSV / AMF；软件 helper `ffmpeg-x264.exe` 因启用 libx264 采用 GPL 配置。QSV / AMF 已编入候选不代表当前电脑实机通过，最终结果以运行时诊断为准。

| 指标 | 含义 |
| --- | --- |
| 平板支持的 Hz | APK 从 Android 原生模式列表读取的能力，例如 60 / 90 Hz。 |
| 目标 Hz / 编码 fps | 当前原生尺寸的请求值，影响 Windows 副屏与视频目标速度。 |
| Android 当前 Hz | Android 当时报告的活动模式；请求 90 Hz 不保证系统已切换。 |
| 已发送帧数 | 已发送的视频访问单元；配置包不计帧，也不证明已在平板显示。 |
| 呈现回调帧/秒 | 电脑按当前连接的递增呈现回调与采样间隔计算，会受回调和采样窗口影响，不等于物理面板测量。 |
| 客户端 fps / decoder | Android 分别报告解码提交、Surface 呈现回调速度和实际解码器名称，诊断中单独记录。 |

H.264 的解码提交和客户端呈现回调分开统计。`render-submitted` 只表示压缩帧已成功送入 MediaCodec 输入队列；`frame-presented` 来自 MediaCodec 的呈现回调。两者都不能单独证明物理面板已达到请求刷新率，且解码提交绝不会被记为呈现回调。合成编码吞吐、Windows Hz、USB 传输和实际观感需分别判断。

### 画质预设与自动码率

当前 0.8.9 Preview 1 保留“自动、低延迟、均衡、高清晰”四种画质预设。这里的“自动画质”与“Auto 编码器”是两个独立设置：前者按链路反馈调整码率/GOP，后者在连接开始时挑选编码后端。无论画质计划怎样重建，本次连接选定的后端都保持不变。

本预览只调整 H.264 目标码率与 GOP。平板报告的原生方向、分辨率和请求刷新率保持不变，自动模式不会降低虚拟显示模式，也不会创建额外显示器。安全桌面暂停、反馈过期或证据不足时冻结判断。`0x16` 接收端反馈包含本会话的接收、队列、提交、呈现和丢弃累计值，但只用于自适应与诊断，不会推进解码提交、呈现回调或显示租约的健康期限。

队列在已建立有效参考链后因溢出或 150 ms 过期而丢失依赖链时，Android 使用已协商的 `decoder-refresh-v1` / `0x15` 限频请求下一枚 IDR。同一 recovery epoch 幂等，暂停期间保留待发 generation；Windows 继续使用同一连接和唯一副屏，等待当前编码器的下一枚自然 IDR。首次等待关键帧、普通重配和正常关闭不会制造恢复请求。

Android 会按分辨率、目标帧率、PerformancePoint、低延迟能力和本进程失败记录为 H.264 decoder 排序，优先使用硬解，并保留其他硬解和软件 decoder 兜底。运行中的 decoder 失败后，备用 decoder 在本机成功启动才请求 `decoder-refresh-v1` 恢复；Windows 保持同一认证连接、同一编码器和同一虚拟副屏，暂停发送依赖帧，下一枚自然 IDR 会在同一个视频包内补齐 SPS/PPS。恢复状态只在备用 decoder 的新呈现回调后结束，不会把解码提交当作用户已经看到画面。

## 连接健康中心

“连接记录”页按顺序显示六个阶段：线路与监听、认证与屏幕参数、唯一虚拟副屏、捕获编码与发送、客户端解码提交、客户端呈现回调。新连接使用独立 attempt 隔离旧回调；断线、暂停和重新连接会更新对应阶段，旧会话的迟到事件不能把新会话错误地标为正常。

“安全修复”只在某个阶段明确进入“需处理”时启用，并执行该阶段允许的有限动作，例如刷新线路、重建连接、配置请求模式或重启视频。涉及显示模式的操作会先停止所有 TabLink 会话，再配置唯一副屏；“打开日志目录”只打开本地记录，不修改显示设备。界面分别显示已发送、客户端解码提交和呈现回调，避免用较早的非零 FPS 掩盖已经停滞的链路。

0.4.2 默认按视频时间戳平滑安排安卓端呈现，90 fps 时目标额外等待约 22.22 ms、未来排程最多 25 ms；这不是整条链路的总延迟。相同动态负载的 30 秒以上 A/B 中，平板最终呈现从 77.665 提高到 **89.837 fps**，P95 间隔从 22.211 降到 **11.121 ms**。慢源、暂停和重连会有界重建时间映射，避免无限排队；完整数据见 `VERIFICATION.md`。

## 功能范围

当前包括一个独立扩展桌面、原生横竖屏匹配、NVENC / QSV / AMF H.264 硬件候选、显式授权的 x264 软件回退、Android 硬解优先并提供软件 decoder 兜底、画面确认、重连、会话守护、排除列表和单指鼠标操作。程序不会自动修改电源计划或升级显卡驱动。

暂不包含音频、压感笔和多点触控。0.8.8 及以后任意时刻只允许一台接收设备占用一块 TabLink 虚拟副屏；可信设备登记数量不会增加显示器上限，也不会创建第三、第四块 TabLink 显示器。刷新速度受捕获、编码、USB、解码与安卓面板策略共同限制，当前版本不承诺所有设备达到 90 fps。同一副屏的位置变化会自动恢复；目标身份、主副屏关系或显示模式发生不兼容变化时会停止采集。

## 文件、日志与构建

完整交付目录包含 Windows 程序、独立更新器、`android/TabLink.apk`、`drivers/VirtualDisplayDriver/`、`tools/ffmpeg/`；不要只复制 `TabLink.exe`。GitHub 的公开 Windows x64 预览包采用 self-contained 构建，无需另装 .NET；普通源码构建默认仍可使用 framework-dependent 模式。

| 位置 | 内容 |
| --- | --- |
| `src/TabLink.Core` | 设备策略、配置、指定序列号的 ADB 调用、屏幕参数解析。 |
| `src/TabLink.Windows` | 界面、显示器身份与生命周期、捕获编码、协议、触控和独立守护。 |
| `src/TabLink.Updater` | Windows 事务安装、启动健康检查、失败回滚和旧版恢复。 |
| `src/TabLink.DriverSetup` | 显式安装、模式配置和特定设备维护。 |
| `android` | 原生 APK、MediaCodec、屏幕参数 provider 与构建脚本。 |
| `native/apple`、`native/harmony` | iOS / iPadOS 与 HarmonyOS NEXT 原生工程及商店更新适配；仍需对应平台签名和实机发布。 |
| `updates`、`tools/TabLink.ReleaseTool` | 签名清单格式、公钥、发布暂存与验签工具。 |
| `tests/TabLink.Core.Tests` | 无需真机的核心回归测试。 |
| `%LOCALAPPDATA%\TabLink\logs\` | 日常连接记录。 |
| `%LOCALAPPDATA%\TabLink\diagnostics\` | 0.7.3 起的屏幕参数、会话健康和守护诊断；旧程序目录中的同名文件仅为历史记录。 |
| `%ProgramFiles%\TabLink\` | Windows 唯一正式运行目录；只有这里允许执行自动替换。 |
| `%ProgramData%\TabLink\Updater\` | 按内容哈希保存、只允许 SYSTEM 与 Administrators 写入的独立更新器。 |
| `%ProgramData%\TabLink\Transactions\` | 与正式目录同卷的受保护暂存、备份、失败版本与更新日志。 |
| `%ProgramData%\TabLink\DisplayLeases\` | 按物理 VDD 目标全局保存的当前显示租约、marker、不可变 bootstrap 与全机文件锁；所有 Windows 用户/会话共用唯一所有权，提升权限的 watcher 只接受这里的精确证据。 |
| `%ProgramData%\TabLink\UsbReverseCleanup\` | 受保护的两阶段 USB reverse 收据、完成 tombstone 与跨进程变更锁；只有当前 Windows 用户 SID 匹配的 `Owned` 可进入精确 ADB 清理。 |
| `%ProgramData%\TabLink\Adb\sha256-<清单哈希>\` | 首次 ADB 交互使用时，从官方 r37.0.0 三件套固定哈希复制并复核的受保护执行副本。 |
| `%ProgramData%\TabLink\NativeTrust\` | 持久电脑证书、主机身份和已登记 Android 公钥；私钥材料另受当前 Windows 用户 DPAPI 保护，发现服务不会公开这里的设备列表。 |
| `%LOCALAPPDATA%\TabLink\display-layouts\` 与 `last-display.json` | 每个 Windows 用户的副屏位置偏好；只影响下次布局，不能授权 watcher 或 ADB 清理。 |
| `%LOCALAPPDATA%\TabLink\updates\` | 已验签的 Windows 下载包、稳定清单、待安装状态及进程握手文件。 |

在源码目录运行：

```powershell
dotnet run --project tests/TabLink.Core.Tests -c Release
.\build.ps1 -SkipAndroid
```

每次推送或 Pull Request 都会同时运行 Windows managed 回归和 Android debug 门禁。Android 作业会校验 Windows / Android 版本一致性，执行纯 JVM 协议测试、`assembleDebug`、`lintDebug` 和 APK 签名验证；它不调用 ADB、不安装驱动，也不访问真实设备。

当前版本身份集中在 `eng/version.json`，根构建、Android 构建和 CI 都会在耗时任务前核对 Windows、Android、build 号与当前文档。普通构建使用 `android/artifacts/TabLink-android-0.8.9-debug.apk`；`-PublicRelease` 会生成不可调试但仍使用既有开发证书的 `TabLink-android-0.8.9-preview.apk`，并注入正式稳定频道地址。公开构建禁止 `-SkipAndroid`，既有预览签名密钥缺失或 APK 签名证书、包名、versionCode、versionName 任一不符合固定发布契约时会失败关闭，不会静默生成新签名身份。公开构建同时生成 self-contained Windows x64 程序、排除不可全球再分发的浏览器接收依赖和 Google ADB 二进制。脚本串行运行可信配对、单屏驱动配置、显示分配、清理与生命周期、连接健康、更新、传输和编码后端回归，复制 APK、LGPL 硬件 helper、GPL x264 helper、各自许可与完整对应源码，并生成 `SHA256SUMS.txt`。构建过程不会安装驱动、创建设备或连接平板。

构建脚本通过 `dotnet TabLink.dll --self-test` 运行纯传输测试，不触发程序启动的 UAC 授权。自测使用系统分配的临时回环端口，不占用实际副屏的 27183，因此可以在现有连接保持时运行。自测只使用合成字节、回环 TCP、临时 E 盘信任目录和 fake input，不捕获桌面、不访问真实 ADB、不更改显示器。当前 Core 代码基线包含 51 个场景，覆盖设备排除、授权重查、模式解析、Android 会话用户固定与切换失败关闭、ADB 环境变量清理、非流式安装结果判定及呈现测量设备绑定等边界；0.8.9 的完整测试数量与结果以 [验证记录](VERIFICATION-0.8.9.zh-CN.md) 的本轮实际输出为准。

## 传输协议 v1

包格式为 `type:uint8 + payloadLength:uint32 big-endian + payload`，JSON 为 UTF-8。PC 接收的认证、触控、确认和屏幕参数包最大 8 KiB，视频包最大 8 MiB。异常长度、截断包、非法确认或 profile 会结束当前会话。

| 方向 | 类型 | 载荷 |
| --- | --- | --- |
| Android → PC | `0x10` | 首次扫码登记：5 分钟内有效且只能使用一次的 `token`、`features`，协商 `trusted-device-v1` 时还必须提供 P-256 SPKI 派生的 `deviceId`、`devicePublicKey`，可附 `deviceName`。旧客户端仍可使用不登记长期信任的原格式，但持久监听同样只接受该短期 bearer 一次。 |
| Android → PC | `0x17` | 已登记设备重连 hello：`protocol`、`deviceId`、`features`；不含 bearer token。 |
| PC → Android | `0x18` | 新挑战：`protocol`、`feature:"trusted-device-v1"`、`hostId`、`deviceId` 和 32 字节随机 `challenge` 的 Base64。 |
| Android → PC | `0x19` | P-256 / SHA-256 ECDSA 证明：`deviceId` 与 DER 签名的 Base64。签名内容绑定协议域、`hostId`、`deviceId` 和本次挑战。 |
| PC → Android | `0x1a` | 首次登记确认：`protocol`、`feature`、`hostId` 与 `deviceId`；Android 随后只保存公开电脑身份、证书指纹和最后地址。 |
| PC → Android | `0x20` | H.264 配置 JSON：`codec:"video/avc"`、`width`、`height`、`fps`、Base64 `csd0` / `csd1`（SPS / PPS），可附 `bitrateKbps`、`generation`、`encoder` 与视频速率上限说明。未知可选字段可忽略；配置包不计视频帧。 |
| PC → Android | `0x21` | `ptsUs:int64 big-endian` 后接一个 Annex B H.264 访问单元，时间戳单位为微秒。 |
| Android → PC | `0x12` | 确认：`kind:"frame-presented"`、递增 `sequence`、`width`、`height`，可附 `fps`、`codec`、`decoder`、`droppedFrames`。 |
| Android → PC | `0x13` | 屏幕参数：`width`、`height`、`rotation`、`activeModeId`、`refreshRate`、`nativeWidth`、`nativeHeight`、`supportedModes`。 |
| Android → PC | `0x14` | 协商后的解码提交进度：`evidence:"render-submitted"`、递增 `frames`、`ptsUs`、`width`、`height`，可附 `fps`、`decoder`；不推进 `0x12` 的呈现回调计数。 |
| Android → PC | `0x15` | 协商后的 decoder 恢复请求：8 字节大端正整数 generation；只请求当前会话的下一枚新 IDR，不推进健康证据。 |
| Android → PC | `0x16` | 协商后的独立接收端反馈：`kind:"receiver-feedback"`、递增 `sequence`，以及接收帧/字节、decoder/recovery epoch、队列深度/容量、提交、呈现和各类丢弃累计值；只用于自适应与诊断。 |
| Android → PC | `0x11` | 鼠标事件：`kind:"down/move/up/scroll"`、归一化 `x` / `y`，滚动可带 `delta`。 |
| PC → Android | `0x02` | UTF-8 状态 JSON；认证后回显 `protocol:1` 和本连接实际协商的 `features`。 |
| PC → Android | `0x03` | UTF-8 错误文本，客户端显示后停止该连接的自动重试。 |
| PC → Android | `0x01` | 保留的 JPEG 诊断与兼容通道；0.4 正常桌面连接采用 H.264。 |

每个 `supportedModes` 项含 `width`、`height`、`refreshRate`、`modeId`。连接前，电脑对选定序列号查询 `content://com.tablink.client.display/capabilities`；连接中用 `0x13` 接收变化。

原生网络路径必须先完成 `0x10` 一次登记，或完成 `0x17` → `0x18` → `0x19` 的新挑战认证，之后电脑才读取 `0x13` 并调用显示准备。撤销后的 `deviceId`、重放旧签名、错误电脑身份、未知或重复认证字段都会在虚拟显示准备之前被拒绝。UDP 27193 的发现 JSON 不属于此 TCP 认证序列，只是为已知 `hostId` 返回当前端口提示。

旧客户端不发送 `features` 时协商结果为空并继续使用既有协议。Android 只有在电脑端分别回显 `render-submitted-v1`、`decoder-refresh-v1`、`receiver-feedback-v1` 后才发送对应的 `0x14`、`0x15`、`0x16`；只有双方同时协商 `receiver-feedback-v1` 与 `adaptive-video-v1`，新版 Windows 才会在同一连接中重复发送 `0x20` 来切换画质计划。安全桌面、DDA 或采集恢复仍可按既有协议重发同一计划的配置，以兼容旧 Windows；重配前后的 `0x21` 媒体 PTS 在整个 TCP 会话内保持严格递增。旧客户端保持连接开始时选定的固定计划；HarmonyOS NEXT 源码本轮仍只声明提交证据能力。未知能力不会回显，任何未协商客户端发送对应扩展消息都会结束异常会话，设备自报的平台或进度类型不能绕过协商。

`0x12` 和 `0x14` 都只随当前连接的新进度推进。重复、倒序、超出本连接已发送视频帧范围或尺寸不匹配的报告不会刷新健康期限。断开和重新认证会清空旧的提交、呈现、客户端 profile、fps 和 decoder。触控坐标对应实际画面，等比显示黑边不产生点击，断开时释放鼠标左键。

## 开源组件与来源

- 显示驱动：[VirtualDrivers / Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)，固定 25.7.23，MIT。许可、签名和哈希记录位于 `third_party/VirtualDisplayDriver/`，交付副本位于 `drivers/VirtualDisplayDriver/`。
- 视频组件：专用于 TabLink 的两个独立 **FFmpeg 7.0.2** helper，都包含 Windows 私有高精度等待补丁和已有缓存帧时的 DDA 非阻塞重复补丁。`ffmpeg.exe` 采用 LGPL-2.1-or-later 配置，提供 NVENC（nv-codec-headers 12.2.72.0）、QSV（oneVPL 2.11.0）和 AMF（AMF 1.4.35）；`ffmpeg-x264.exe` 启用固定 x264 stable 源码并采用 GPL-2.0-or-later 配置。两者都不含网络协议，不修改系统计时器、注册表或显卡驱动。
- 0.8.5 最终 E 盘构建：`ffmpeg.exe` SHA-256 `BB1FA5F2A5CC572C6A1D310F88348324EE43B84DF5A778FD0AF02D77B3C86627`，`ffmpeg-x264.exe` SHA-256 `6E3EA733AD40DA6D6D78C2DFC51BCCA950C3519D3304AE045316F7D55B89EDB7`，`source-bundle.tar.gz` SHA-256 `FD7977F53EDD262D55C49F200EB5F54B1B12F5FFA547770380448708D75EA6F2`。构建使用中性 prefix，并对个人路径做 fail-closed 检查。公开仓库在 `third_party/ffmpeg-tablink/` 保留补丁、来源、许可和可复现构建说明；发布二进制时须附完整对应源码。helper 作为单独进程运行，没有替换系统 FFmpeg。
- USB 工具：Wi-Fi 与普通 USB 网络共享不使用 ADB；可选的 USB 调试兼容路径只接受官方 Windows Platform-Tools r37.0.0 固定哈希三件套，首次交互时复制到受保护 ProgramData 后再执行。公共包不分发 Google ADB 二进制。
