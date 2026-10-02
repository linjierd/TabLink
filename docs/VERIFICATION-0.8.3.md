# TabLink 0.8.3 Preview 1 验证记录

验证日期：2026-10-01（Asia/Shanghai）

本文记录 0.8.3 Preview 1 的画质策略、接收端反馈、队列关键帧恢复、同连接编码器重配和既有单副屏生命周期回归。真实网络限速、真实 MediaCodec 故障和物理面板表现会明确保留为现场验证项。

## 候选版边界

- Windows 项目版本：`0.8.3`，预期 FileVersion `0.8.3.0`。
- Android：`versionName 0.8.3`、`versionCode 15`。
- 协议主版本仍为 v1；新增 `receiver-feedback-v1` / `0x16` 与 `adaptive-video-v1`。
- `decoder-refresh-v1` / `0x15` 继续使用固定 8 字节大端正 generation。
- Apple 与 HarmonyOS NEXT 仍为 0.8.0；本轮不生成 IPA 或 HAP。
- 公网 stable 更新清单继续指向 0.8.0。
- 公共 Windows 包采用 self-contained x64，并排除 Google ADB 二进制和 SIPSorcery 浏览器组件。

## 自动化验收范围

候选提交必须完成：

1. 仓库全部托管测试项目；
2. `TabLink.Video.Tests` 的四种预设、原生模式保持、降档/冷却/回升、暂停与过期反馈冻结、跨编码器 PTS；
3. `TabLink.Transport.Tests` 的能力交集、合法 `0x16` 保存、遥测与健康证据分离、未协商/越界/倒退反馈关闭，以及既有 60 次连接竞态；
4. `TabLink.Browser.Tests`，确认原有固定浏览器路径没有被原生自适应改动破坏；
5. Android 纯 JVM 协议、`VideoFrameQueue` recovery epoch、`KeyFrameRequestController`、`PendingDecoderRefresh`、`ReceiverFeedbackProgress`、RenderClock、HUD、配对/TLS 与稳定更新测试；
6. Android `assembleRelease`、`lintRelease` 与 `apksigner verify --verbose`；
7. Windows 浏览器功能启用与公共裁剪两种 Release 编译；
8. 发布目录 `TabLink.dll --self-test` 和 `SHA256SUMS.txt` 逐项复核。

## 已完成的工作树回归

| 项目 | 状态 | 证据边界 |
| --- | --- | --- |
| `TabLink.Video.Tests` | 通过 | 13 项 PASS；包括 5 项新增画质/PTS策略测试。测试不采集真实桌面。 |
| `TabLink.Transport.Tests` | 通过 | 21 项 PASS；包括新能力与 `0x16` fail-closed，并保留 60 次并发释放竞态。 |
| `TabLink.Browser.Tests` | 通过 | 32 项 PASS；浏览器路径仍为固定兼容画质。 |
| 其余 Windows 托管测试 | 通过 | Core 30、ADB 定位 19、驱动配置 33、显示分配 30、显示清理 18 个场景 / 78 断言、显示身份 40、显示生命周期 82、连接健康 13 个场景 / 45 断言、诊断 14 个场景 / 94 断言、USB 租约 14、稳定更新 17 个场景 / 119 断言全部通过。 |
| Windows 公共裁剪编译 | 通过 | `EnableBrowserReceiver=false`，Release 为 0 warning / 0 error。 |
| Windows 完整预构建 | 通过 | 浏览器功能启用的 Release 发布完成；`TabLink.exe` FileVersion 为 `0.8.3.0`；发布目录以 `dotnet TabLink.dll --self-test` 完成 21 项 PASS。直接执行声明 UAC 的 apphost 不作为无人值守自测入口。 |
| Android 纯 JVM 回归 | 通过 | 协议/适配 75、视频队列 1,973、关键帧限频 19、pending/恢复代次竞态 27、接收端反馈 16、RenderClock 26,024、decoder 选择 13、HUD/暂停 20、配对/TLS 108、稳定更新 44 项断言全部通过；不运行真实 MediaCodec。 |
| Android Release 构建 | 通过 | `assembleRelease`、`lintRelease` 成功（0 error / 18 warning）；APK v1/v2 验签通过；`aapt2` 确认 `versionName 0.8.3` / `versionCode 15`。预提交 APK SHA-256：`95212D2156DBDA5A73657212926A70ECFD9BA407E96D7C580621C2BB7B3175C5`。 |

上表是提交前工作树回归。最终公开 ZIP、APK、完整测试集、文件版本、签名和 SHA-256 只能由干净提交上的 `-PublicRelease` 构建与 GitHub Release 资产证明，不能由上表提前代替。

## 必须保持的安全与兼容条件

- 自动模式只改变编码计划，不改变虚拟显示器原生尺寸、方向或请求刷新率。
- 本预览运行中只调整码率/GOP；不降低显示分辨率，不创建第三或第四块显示器。
- 未同时协商两项新能力时，不得因画质计划变化而中途重配 `0x20`；安全桌面、DDA 或采集恢复仍可按既有协议重发同一计划的配置。
- 每次编码器重建后的媒体 PTS 必须大于本连接此前所有 PTS。
- `0x16` 不得推进 submitted、presented、显示租约或 20 秒呈现期限。
- 没有活动 decoder 时不发送 queueCapacity 为 0 的反馈。
- 初始等待 IDR、重复依赖帧、clear、close 和 decoder 重配不产生队列恢复 epoch。
- `0x15` 本地请求限制为 burst 2 / refill 1 每秒；暂停竞态不能永久丢失已授权 generation。
- 新一代 generation 不能被旧异步回调覆盖或清除。
- 任何显示回收仍只使用本次拥有的精确租约，不按友好名称批量删除设备。
- 构建和自动化测试本身不得安装驱动、创建虚拟屏或连接平板。

## 尚待现场验证

| 项目 | 状态 |
| --- | --- |
| W202DS 覆盖安装 0.8.3 / versionCode 15 | 待最终 APK 构建后验证 |
| `0x16` 在真实 90 Hz 流中持续发送且不造成断线 | 未验证 |
| 人为限制 Wi-Fi 带宽后 Auto 降档、稳定和回升 | 未验证 |
| 手动切换四种预设时同一 TLS/TCP 与唯一副屏保持 | 未验证 |
| 队列真实溢出/过期后 `0x15` 恢复且不重建显示器 | 未验证 |
| SurfaceFlinger 与物理面板帧率 | 未验证 |
| Apple / HarmonyOS 新能力 | 不在本轮范围 |

历史 0.4.2 的 W202DS `89.702 fps` 长样本与 0.8.1 的单副屏驱动生命周期只作为回归背景，不属于 0.8.3 的现场通过证据。
