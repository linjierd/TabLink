# TabLink 0.8.2 Preview 2 验证记录

验证日期：2026-10-01（Asia/Shanghai）

本文记录 0.8.2 Preview 2 的 Windows 连接健康中心、原生协议能力协商、Android decoder 候选评分、恢复协议与 Windows 同会话门控的自动化验证。真实 MediaCodec 切换和未执行的现场步骤会明确标成“未验证”，不会用 Preview 1、0.8.1 或更早版本的结果替代。

## 候选版边界

- Windows 项目版本：`0.8.2`，预期 FileVersion `0.8.2.0`。
- Android：`versionName 0.8.2`、`versionCode 14`。
- 原生协议仍为 v1；可选能力包括 `render-submitted-v1` / `0x14` 和 `decoder-refresh-v1` / `0x15`。
- Apple 与 HarmonyOS NEXT 本轮仍为 0.8.0；本轮没有生成或发布 IPA、HAP。
- 公网 stable 更新清单仍指向 0.8.0。
- 公共 Windows 包采用 self-contained x64，排除 Google ADB 二进制和 SIPSorcery 浏览器组件。

## 自动化验收

最终候选从干净提交和全新 E 盘目录依次通过：

1. 全部 14 个托管测试项目；
2. `TabLink.ConnectionHealth.Tests` 的 attempt 隔离、阶段单向推进、暂停/恢复、提交与呈现分离和安全恢复动作；
3. `TabLink.Transport.Tests` 的旧 HELLO、已知/未知 feature 协商、任意未协商 `0x14` / `0x15` 拒绝、submitted/presented 独立计数与 FPS、decoder refresh 连接隔离、generation 幂等/合并/限频、P 帧门控、SPS/PPS + 新 IDR 组合，以及伪造或倒序进度关闭；
4. Windows 浏览器功能启用和公开裁剪两种 Release 编译；
5. 发布目录中的 `TabLink.dll --self-test`；
6. Android 纯 JVM 协议、队列、节拍、HUD/暂停、配对/TLS 与稳定更新测试；
7. Android `assembleRelease`、`lintRelease` 和 `apksigner verify --verbose`；
8. 全新空目录中的 `build.ps1 -PublicRelease`，以及发布目录 `SHA256SUMS.txt` 逐项复核。

发布流程先在干净提交上完成整套构建与审计，再固化本文，最后从新的空目录重复同一套公开构建。最终标签、Windows `ProductVersion` 和 GitHub Release 说明共同记录最终提交；外层发布资产哈希记录在 Release 的 `SHA256SUMS.txt` 中。

## 必须验证的行为

- 六个健康阶段按真实连接顺序推进；新 attempt 拒绝旧回调。
- 主会话、原生配对页、浏览器和 ADB 兼容路径都投影到同一健康中心。
- 客户端断开后，健康状态从认证阶段开始等待重连，不保留整页全绿。
- submitted 或 presented 证据过期时，对应阶段和 FPS 降级，不展示旧速度为当前速度。
- `0x14` 只有在本连接协商 `render-submitted-v1` 后才被接受；客户端自报的平台或证据类型不能绕过协商。它绝不增加 presented 计数。
- `0x15` 只有在本连接协商 `decoder-refresh-v1` 后才被接受；载荷必须是 8 字节大端正 generation。它不重启编码器、TCP、显示准备或虚拟副屏，也不推进 submitted/presented 健康证据。
- decoder refresh pending 时，非 IDR 不进入发送帧计数和 recent PTS；下一枚新 IDR 在同一个 `0x21` 内包含 NAL 7、8、5，不额外发送 `0x20`。
- Android 只有在备用 MediaCodec 成功启动后请求恢复；等待状态只由真实 `OnFrameRenderedListener` 回调结束，`onSubmitted` 不能提前标成恢复。
- 显示模式配置会先停止所有 TabLink 会话。
- 活动虚拟显示回收失败时保留“需处理”状态，只重试本次拥有的精确租约；不按名称删除其他虚拟显卡。
- 任意时刻最多存在一个 TabLink 显示租约和一个 TabLink 扩展目标。

## 构建与制品结果

| 项目 | 状态 | 证据 |
| --- | --- | --- |
| 精确 Git 提交与干净工作树 | 通过 | `-PublicRelease` 在编译前强制检查 Git 工作树；非干净工作树会中止，最终候选来自干净提交和全新空目录 |
| 14 个托管测试项目 | 通过 | 14 个项目全部通过；其中 Browser 32 项、DisplayCleanup 18 个场景 / 78 个断言、ConnectionHealth 13 个场景 / 45 个断言、Transport 含 60 次竞态、Update 17 个场景 / 119 个断言 |
| Windows 两种 Release 配置 | 通过 | 默认浏览器接收配置与 `EnableBrowserReceiver=false` 公开裁剪配置均为 0 warning / 0 error |
| Windows 发布自测 | 通过 | 发布目录执行 `dotnet TabLink.dll --self-test`，`selftest-result.txt` 共 21 项 PASS |
| Windows ProductVersion / FileVersion | 通过 | FileVersion `0.8.2.0`；ProductVersion 为 `0.8.2+` 加本次构建的完整 40 位提交 SHA |
| Android 协议及逻辑测试 | 通过 | 协议、恢复载荷与画面适配 72；DecoderCandidateSelector 13；VideoFrameQueue 1,957；RenderClock 26,024；HUD / 暂停 20；配对 / QR / TLS 108；稳定更新 44，全部通过 |
| Android release / lint / 签名 | 通过 | `assembleRelease`、`lintRelease` 成功；Lint 0 error / 18 warning；APK v1、v2 签名验证通过 |
| 公共 Windows 包文件与哈希 | 通过 | 除清单本身外的 452 个文件与 `SHA256SUMS.txt` 的 452 条记录一一对应，逐项哈希一致；无缺失、重复、未列出或目录逃逸 |
| 构建前后活动 MttVDD 数量 | 通过 | 构建前 0，构建后 0；构建过程没有安装驱动或创建显示设备 |

公共 Windows 包没有 PDB、`adb.exe`、SIPSorcery、源代码、签名私钥/密钥库或个人绝对路径。公开文档的相对链接可解析，`vdd_settings.xml` 只有一个 monitor 定义。precommit 普通构建是开发用全量目录，不作为公开裁剪证据。

公开包的定制 FFmpeg 7.0.2 使用中性 `/ffmpeg-tablink` prefix，2,393,600 字节，SHA-256 为 `A9B13FC5B5D287FD7EADB39C4755B84F6FEA44A7CE10DC8AC8BD7FFDA66FBBEC`。两个不同 E 盘暂存目录的完整构建经确定性 strip 后逐字节一致；二进制扫描与 `ffmpeg -version` 均没有构建者账号或项目绝对路径。项目真实 `H264Encoder` 使用该文件完成 1200 × 1920、请求 90 fps 的 450 / 450 帧合成 NVENC、Annex-B 解析、并发释放及异常宿主 JobObject 回收测试。对应源码包 SHA-256 为 `4F08A1E8FEF87E91AB9B0915610DF19AA80D69BA52F55D025BC7D964384293FD`；源码包的验签日志仅中性化临时 keyring 路径，两个 Good signature、签名者与指纹保持不变。公开构建会先把 FFmpeg 二进制和源码包与受版本控制的 `SHA256SUMS` 精确绑定，再拒绝绝对路径、`..` 逃逸、大小写冲突和链接条目，验证关键对应源码与许可存在，解包扫描并拒绝带盘符 `Users` 或 `/c/Users/` 类个人路径，随后清理临时审计目录。

上游 `mttvdd.cat` 与 `MttVDD.dll` 的固定 SHA-256 与来源记录一致，PowerShell `Get-AuthenticodeSignature` 和 SignTool 通用 Authenticode 策略 `/pa` 均通过；catalog 覆盖包内精确的 INF 与 DLL，INF 文本本身没有独立的嵌入签名。该签名不是 Microsoft WHQL 或 attestation 签名；本机 `signtool verify /kp` 对 catalog 返回“不受驱动 trust provider 信任的根”，因此本记录不声称通过 Windows 内核/驱动策略验签，也不据此声称所有电脑都能安装。TabLink 不会安装证书、启用测试签名、关闭安全启动或降低系统签名策略。完全相同哈希的驱动文件曾在 2026-09-30 的 0.8.1 本机真机生命周期中成功按需安装并正常回收，但那是历史兼容性证据，不替代 0.8.2 的独立验收。本轮没有执行 0.8.2 驱动安装；目标 Windows 的实际接受或拒绝属于下方真机验收范围。

Android 候选包为 `android/artifacts/TabLink-android-0.8.2-preview.apk`。`aapt dump badging` 确认包名 `com.tablink.client`、`versionCode 14`、`versionName 0.8.2`、minSdk 23、targetSdk 35；签名证书 SHA-256 为 `B0035FFE0539E43DED2F5C40E3B7E4D4EDFB5D8F8063459FACA911EDC7500554`。该 release APK 从干净提交构建，并使用既有开发证书，以便测试设备覆盖升级；它不是应用商店生产签名。最终 APK 字节数和 SHA-256 由同次 GitHub Release 外层 `SHA256SUMS.txt` 绑定。显示与 pacing ContentProvider 由 `android.permission.DUMP` 限制读取，普通第三方应用不能直接轮询实时 codec 指标；目标平板上的 ADB shell 读取行为尚未实机确认。

## 真机验收

以下项目在最终 APK 安装到实机并以最终 Windows 候选运行之前均为“未验证”：

| 项目 | 状态 |
| --- | --- |
| Android 0.8.2 覆盖安装及 versionCode 14 | 未验证 |
| 目标平板通过 ADB shell 读取受 `DUMP` 保护的能力接口 | 未验证 |
| 上游虚拟显示驱动被当前 Windows 驱动策略接受 | 未验证 |
| Wi-Fi 原生连接 | 未验证 |
| USB 网络连接 | 未验证 |
| ADB 兼容连接 | 未验证 |
| 六阶段健康中心到达和断线降级 | 未验证 |
| `0x14` 解码提交与 `0x12` 客户端呈现回调独立显示 | 未验证 |
| 真机注入 MediaCodec 故障后，同一 TLS/TCP、同一副屏自动切换并恢复 | 未验证 |
| 真机确认备用 decoder 等待状态只在新呈现回调后结束 | 未验证 |
| UAC / 安全桌面暂停后恢复 | 未验证 |
| Windows 显示位置变化后恢复 | 未验证 |
| 断开后活动 TabLink 虚拟显示设备为 0 | 未验证 |

历史 W202DS 上 0.8.1 的 1920 × 1200、90 Hz 单副屏生命周期，以及 0.4.2 的 SurfaceFlinger 89.702 fps 长样本，不属于 0.8.2 的现场通过证据。
