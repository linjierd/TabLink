# TabLink 0.8.6 Preview 1 验证记录

验证日期：2026-10-01（Asia/Shanghai）

本文只记录 0.8.6 的 USB 会话恢复、随机 reverse 端点、停止交错、watcher 清理和呈现期限加固。0.8.5 的 90 fps 捕获与物理呈现测量继续保留在 `VERIFICATION-0.8.5.md`，不能替代本版的故障恢复验证。

## 安全与生命周期不变量

- 只在本次 FrameServer 曾经完成可信客户端认证后启动 USB 恢复。
- 每条目标 ADB 命令均使用 `-s <原批准设备>`，并在执行前重新核验 ADB 状态、Windows USB 身份和当前排除规则。
- 设备端端口由 CSPRNG 从 49152–65535 选择；电脑端目的端口固定为 loopback 27183。已有候选只会被跳过，不会被接管、替换或删除。
- `--no-rebind` 一旦越过最后取消边界，使用自己的 15 秒期限完成。停止流程会 drain 初次建链或恢复任务，再快照并清理精确端点。
- 一次断线最多三次恢复；恢复不调用显示分配器或驱动安装器，不可能创建第二块虚拟副屏。
- watcher 只对原 `DisplayLease` 执行 detach。USB 临时失败不会阻止 marker 退休和驱动回收；只有 `Removed` / `AlreadyAbsent` 才消费 reverse 收据。
- schema 2 收据写入 `ReverseLeaseV2`；旧读者忽略它，新读者也不把旧 `ReverseLease` 当作随机端点删除权限。

## 自动验证

- `TabLink.Core.Tests`：39 项通过。
- `TabLink.UsbRecovery.Tests`：12 个场景、129 条断言通过。
- `TabLink.UsbLease.Tests`：19 条断言通过。
- `TabLink.DisplayLifecycle.Tests`：134 条断言通过。
- `TabLink.DisplayCleanup.Tests`：20 个单屏生命周期场景、86 条断言通过。
- `TabLink.Transport.Tests`：21 个传输与认证场景通过。
- `TabLink.Browser.Tests`：37 个浏览器安全、媒体与单屏生命周期场景通过。
- Windows Release 编译：0 个警告、0 个错误。
- Android JVM、Gradle、lint 与 APK 签名检查由最终完整构建串行执行。

上述测试使用 fake ADB、fake display adapter、注入时钟和内存生命周期对象，不访问真实平板、手机或虚拟显示驱动。真实设备结果单独记录，不能由离线测试推断。

## 实机 USB 故障注入

2026-10-01 在一台已授权的中兴 W202DS 上完成了 0.8.6 候选版验证。Android 包管理器确认安装的是 `versionName 0.8.6`、`versionCode 18`、包名 `com.tablink.client`。电脑端启动时重新核验了唯一授权目标；没有向 F50 发出命令，也没有执行 `kill-server`、`start-server`、`--remove-all` 或无 `-s` 目标的设备命令。

建立会话后确认：

- 只有一个 CSPRNG 随机设备端 reverse endpoint 指向电脑 loopback 27183，且 27183 同时存在一个 FrameServer listener 和一个已建立连接。
- Windows 只有一块 TabLink `Virtual Display Driver`；Oray 与 ToDesk 的既有虚拟显示适配器不属于 TabLink，验证期间没有被修改。
- 平板画面已实际呈现。Android HUD 显示面板 90 Hz、请求 90 Hz、解码提交 90.1 fps、呈现回调 90.0 fps，使用 `c2.unisoc.avc.decoder` 首选硬解。

故障注入只删除了这次 TabLink 会话拥有的精确随机 endpoint，随后对同一台 W202DS 执行目标明确的 `am force-stop com.tablink.client`，使既有 socket 断开并触发真实恢复路径。结果如下：

- 10.2 秒内自动重建同一个随机 endpoint、重新启动 Android 客户端并重新建立 TCP 连接。
- Windows TabLink 进程集合、FrameServer PID、会话内的虚拟显示实例 ID 均保持不变。
- 恢复期间和恢复后始终只有一块 TabLink 虚拟显示器；恢复代码没有调用显示分配器或安装第二块屏幕。
- 恢复后的实机截图确认桌面继续全屏呈现，HUD 显示解码提交 90.1 fps、呈现回调 90.0 fps，并标记硬件解码“已恢复”。

最后通过 `--exit` 正常停止候选程序。40 秒清理窗口内实际在约 6 秒完成：TabLink 进程数为 0、该设备上的 TabLink 随机 reverse endpoint 数为 0、TabLink VDD 数为 0；Oray 与 ToDesk 两个既有虚拟适配器仍然存在。由此确认正常停止会精确删除本会话映射、分离原显示并卸载本次拥有的虚拟显示设备。

## 最终二进制与公开包

- Windows FileVersion：0.8.6.0；ProductVersion 使用 `0.8.6+<发布提交>`。
- Android：`versionName 0.8.6`，`versionCode 18`，包名 `com.tablink.client`。
- 已签名的公网稳定更新清单保持 0.8.0。
- Windows ZIP、Android APK 与外层校验文件的最终字节数和 SHA-256 由干净提交的 `-PublicRelease` 构建生成，并记录在 GitHub Release 的 `SHA256SUMS.txt`。

## 已知边界

- ADB 的 `reverse --list` 与 `reverse --remove` 不是原子 compare-and-delete。随机端点显著降低正常 ABA 碰撞，但无法抵御另一个本机进程主动竞态。
- 平板持续离线时，旧随机 reverse 收据可能在后续新显示会话中失去重试入口；这不会阻塞 VDD/驱动回收，也不会扩大删除权限。
- Intel QSV、AMD AMF、Apple 原生客户端和 HarmonyOS 原生客户端没有在本次 USB 恢复验证中增加新的实机结果。
