# TabLink 0.8.2 验证记录

验证日期：2026-10-01（Asia/Shanghai）

本文记录 0.8.2 Windows 连接健康中心、原生协议能力协商、Android 解码提交证据和单副屏生命周期的独立验证。未执行的现场步骤会明确标成“未验证”，不会用 0.8.1 或更早版本的结果替代。

## 候选版边界

- Windows 项目版本：`0.8.2`，预期 FileVersion `0.8.2.0`。
- Android：`versionName 0.8.2`、`versionCode 13`。
- 原生协议仍为 v1；新增可选能力 `render-submitted-v1` 和消息 `0x14`。
- Apple 与 HarmonyOS NEXT 本轮仍为 0.8.0。
- 公网 stable 更新清单仍指向 0.8.0。
- 公共 Windows 包采用 self-contained x64，排除 Google ADB 二进制和 SIPSorcery 浏览器组件。

## 自动化验收计划

最终候选必须在同一精确源码上依次通过：

1. 全部 14 个托管测试项目；
2. `TabLink.ConnectionHealth.Tests` 的 attempt 隔离、阶段单向推进、暂停/恢复、提交与呈现分离和安全恢复动作；
3. `TabLink.Transport.Tests` 的旧 HELLO、已知/未知 feature 协商、Android / Harmony 源码协商、任意未协商 `0x14` 拒绝、submitted/presented 独立计数与 FPS、伪造或倒序进度关闭；
4. Windows 浏览器功能启用和公开裁剪两种 Release 编译；
5. 发布目录中的 `TabLink.dll --self-test`；
6. Android 纯 JVM 协议、队列、节拍、HUD/暂停、配对/TLS 与稳定更新测试；
7. Android `assembleRelease`、`lintRelease` 和 `apksigner verify --verbose`；
8. 全新空目录中的 `build.ps1 -PublicRelease`，以及发布目录 `SHA256SUMS.txt` 逐项复核。

最终测试数量、警告数、文件数、提交 SHA 和制品哈希只在实际命令成功后填写。

## 必须验证的行为

- 六个健康阶段按真实连接顺序推进；新 attempt 拒绝旧回调。
- 主会话、原生配对页、浏览器和 ADB 兼容路径都投影到同一健康中心。
- 客户端断开后，健康状态从认证阶段开始等待重连，不保留整页全绿。
- submitted 或 presented 证据过期时，对应阶段和 FPS 降级，不展示旧速度为当前速度。
- `0x14` 只有在本连接协商 `render-submitted-v1` 后才被接受；客户端自报的平台或证据类型不能绕过协商。它绝不增加 presented 计数。
- 显示模式配置会先停止所有 TabLink 会话。
- 活动虚拟显示回收失败时保留“需处理”状态，只重试本次拥有的精确租约；不按名称删除其他虚拟显卡。
- 任意时刻最多存在一个 TabLink 显示租约和一个 TabLink 扩展目标。

## 构建与制品结果

待最终集成构建完成后填写：

| 项目 | 状态 | 证据 |
| --- | --- | --- |
| 精确 Git 提交与干净工作树 | 待验证 | — |
| 14 个托管测试项目 | 待验证 | — |
| Windows 两种 Release 配置 | 待验证 | — |
| Windows 发布自测 | 待验证 | — |
| Windows ProductVersion / FileVersion | 待验证 | — |
| Android 协议及逻辑测试 | 通过 | 协议与画面适配 66；VideoFrameQueue 1,957；RenderClock 26,024；HUD / 暂停 20；配对 / QR / TLS 108；稳定更新 44，全部通过 |
| Android release / lint / 签名 | 通过 | `assembleRelease`、`lintRelease` 成功；Lint 0 error / 17 warning；APK v1、v2 签名验证通过 |
| 公共 Windows 包文件与哈希 | 待验证 | — |
| 构建前后活动 MttVDD 数量 | 待验证 | — |

Android 候选包为 `android/artifacts/TabLink-android-0.8.2-preview.apk`，共 315,034 字节，SHA-256 为 `72C3CF2AA930B61E672361221B1C77B359A5081BE50907F2016A86DAA51B18AB`。`aapt dump badging` 确认包名 `com.tablink.client`、`versionCode 13`、`versionName 0.8.2`、minSdk 23、targetSdk 35。该 release APK 使用既有开发证书，以便测试设备覆盖升级；不是商店生产签名。

## 真机验收

以下项目在最终 APK 安装到实机并以最终 Windows 候选运行之前均为“未验证”：

| 项目 | 状态 |
| --- | --- |
| Android 0.8.2 覆盖安装及 versionCode 13 | 未验证 |
| Wi-Fi 原生连接 | 未验证 |
| USB 网络连接 | 未验证 |
| ADB 兼容连接 | 未验证 |
| 六阶段健康中心到达和断线降级 | 未验证 |
| `0x14` 解码提交与 `0x12` 实际呈现独立显示 | 未验证 |
| UAC / 安全桌面暂停后恢复 | 未验证 |
| Windows 显示位置变化后恢复 | 未验证 |
| 断开后活动 TabLink 虚拟显示设备为 0 | 未验证 |

历史 W202DS 上 0.8.1 的 1920 × 1200、90 Hz 单副屏生命周期，以及 0.4.2 的 SurfaceFlinger 89.702 fps 长样本，不属于 0.8.2 的现场通过证据。
