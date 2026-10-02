# TabLink 0.8.7 Preview 1：受保护的 USB 清理队列

发布日期：2026-10-01

TabLink 0.8.7 Preview 1 收拢 0.8.6 留下的 ADB USB 清理边界。每次成功创建的随机 `adb reverse` 端点都有独立、不可被后续显示会话覆盖的持久记录；平板暂时离线或 ADB 不可用时，记录留在受保护队列中等待以后重试。USB 清理仍与虚拟显示回收分开，任何 USB 清理失败都不能阻止 TabLink 收回自己拥有的一块副屏和对应活动驱动实例。

## 本次变化

### 受保护的持久队列

- 自动清理权限只保存在 `%ProgramData%\TabLink\UsbReverseCleanup\`。TabLink 创建并复核受保护目录、文件、owner、DACL、父路径和重解析点边界；普通用户可修改的设置、环境变量、`PATH` 或 `%LOCALAPPDATA%` 文件不能成为管理员进程执行 ADB 删除的依据。
- 每条 `UsbReverseLease` 使用独立 GUID 文件，不再依附单个显示租约槽位。新的显示会话不会覆盖旧会话仍待处理的 USB 收据。
- 收据升级为 schema 3，并绑定原进程 PID、启动时间与 Windows 用户 SID。跨用户的 `Owned` 记录不会由当前用户执行 ADB；没有删除权限的 `Prepared` 记录则可直接安全封存，不需要接触设备。
- 完成记录先持久化为 tombstone，再删除待处理文件。若进程恰好在两步之间退出，下一次读取会以 tombstone 为准，不会重新授予已经消费的删除权限。
- 单次清理最多检查四条记录。队列在记录内保存严格递增的重试序号，暂时无法完成的条目会获得新的队尾序号；顺序不依赖文件系统时间精度，因此一条离线设备记录不会长期饿死后续可处理记录。

### `Prepared` → `Owned` 两阶段授权

1. 选择新的随机设备端端口后，TabLink 先写入 `Prepared` 记录。该状态只保留端点并防止重复分配，**没有 ADB 删除权限**。
2. TabLink 对明确选择、重新核验且未被排除的设备执行精确的 `reverse --no-rebind`。
3. 只有该命令明确成功后，同一份不可变收据才会原子提升为 `Owned`。只有 `Owned` 记录可以进入精确清理。
4. 如果程序在命令成功与 `Owned` 持久化之间退出，可能留下一个随设备断开或 ADB 重启而消失的映射；TabLink 宁可保留该映射，也不会根据不完整记录推断删除权限。

`Prepared` 记录在重试时会永久封存为无删除动作的完成记录。`Owned` 记录仍须重新核验原序列号、VID/PID、当前排除规则、精确随机端点和现有 reverse 目标，并在删除前再次读取同一 reverse 表；`Removed`、`AlreadyAbsent`、确认已被其他映射取代或安全封存后才消费记录。TabLink 不使用 `--remove-all`、隐式默认设备、`kill-server`、`start-server`、`tcpip` 或先删除再重绑。

### 固定身份的 ADB 执行文件

- 自动清理不执行用户设置中的任意 ADB 路径。ADB 兼容模式只接受 Google 官方 **SDK Platform-Tools r37.0.0 for Windows** 中与内置 SHA-256 完全匹配的 `adb.exe`、`AdbWinApi.dll` 和 `AdbWinUsbApi.dll`。
- 首次刷新设备、安装 APK 或连接等交互 ADB 操作时，三个文件按固定清单身份复制到 `%ProgramData%\TabLink\Adb\sha256-<清单哈希>\`，随后再次复核文件集合、哈希、owner、DACL 与重解析点，再从受保护目录启动。
- GitHub 公共 Windows ZIP 继续不分发 Google Android SDK Platform-Tools。用户首次在交互界面选择一次固定哈希匹配的官方 r37.0.0 三件套后，受保护副本可供后续后台清理复核并复用；尚未完成这次 staging 时，自动清理只保留队列，不会回退到 `PATH`、Android SDK 或用户设置中的源路径。
- Wi-Fi 与普通 USB 网络共享继续使用 TLS 原生网络通道，不调用 ADB、不要求 USB 调试，也不受 Platform-Tools 是否存在影响。

### 显示回收继续独立

- 当前显示租约、marker 与不可变 bootstrap 也迁入 `%ProgramData%\TabLink\DisplayLeases\`，通过受保护文件和全机文件锁协调 UI 与 watcher。每个用户的 `.last.json` / `last-display.json` 仍只作为 LocalAppData 布局偏好，不能授权提升权限的显示回收。
- TabLink 空闲、只打开配对页或等待扫码时不保留活动虚拟显示设备。接收端完成认证并提交有效屏幕参数、连接即将占用副屏时，电脑才检查并按需安装唯一的 TabLink 虚拟显示设备；正常断开、超时、连接准备失败或主程序异常退出后，必须移除该会话确证拥有的活动设备。签名驱动包和单输出模式配置留在电脑中，供下次连接复核后重建。
- watcher 仍先按原 `DisplayLease` 分离精确显示目标、退休 marker 并移除本次拥有的活动虚拟显示设备，再以有界方式处理 USB 队列。watcher 启动失败时，精确 guard 会交还分配器继续回收，而不会留下同进程无法重试的活动 marker。
- 队列损坏、受保护存储不可用、平板离线、排除规则变化、ADB 缺失或映射冲突只会留下诊断和待处理记录；这些情况不会扩大显示设备权限，也不会把副屏留在 Windows 中。
- 全局仍只允许一个 TabLink 显示租约和一块 TabLink 扩展屏；已有连接或正在准备连接时，第二个接收端必须在驱动变更前被拒绝。

### 版本与兼容性

- Windows 主程序版本为 `0.8.7`。
- Android 预览 APK 为 `versionName 0.8.7`、`versionCode 19`（build 19）。本轮 Android 只同步版本号；视频、输入、配对和更新协议没有变化。
- 原生协议主版本仍为 v1。0.8.5 的 90 fps 修复、0.8.4 的多编码器支持、0.8.3 的自适应画质和 0.8.2 的健康/decoder 恢复能力保持不变。
- 已签名公网 `stable` 自动更新频道继续保持 0.8.0；发布本预览不会推进稳定清单。

## 已知边界

- ADB 的 `reverse --list` 与 `reverse --remove` 没有原子 compare-and-delete。TabLink 通过随机端点、受保护全机变更门、精确设备身份和删除前第二次映射核验降低普通竞态，但无法消除另一个本机管理员进程在最终核验后、删除命令前主动抢占同一端点的极短竞态。
- `%LOCALAPPDATA%\TabLink\display-leases` 中由 0.8.6 或更早版本写入的旧 USB 收据只能用于诊断，不能迁移成 0.8.7 的管理员删除权限；旧显示状态也不能授权新版 watcher。升级前已经遗留的旧 reverse 映射可能要等设备断开或 ADB 服务重启自然消失。
- 公共包不含 Platform-Tools；在用户尚未选择并成功 staging 官方固定哈希 r37 三件套前，队列会安全保留且 ADB 后台清理延期。成功 staging 一次后，公开包可复用受保护缓存执行后续精确清理。
- 本版尚未加入长期可信设备密钥、自动发现、跨 IP 自动重连、音频、压感笔、多点触控或多接收设备。

## 安装

Windows 公共 ZIP 为 self-contained x64 包。完整解压后运行 `TabLink.exe`；程序会请求管理员权限，用于按连接生命周期维护唯一虚拟显示设备和受保护清理状态。Android 可覆盖安装 `TabLink-Android-0.8.7-preview.1.apk`，现有设置会保留。

最终本地候选的自动测试、受保护存储检查，以及 W202DS 正常连接、断开清理和重新连接结果已记录在 [VERIFICATION-0.8.7.md](VERIFICATION-0.8.7.md)。公开构建、GitHub Release 资产和发布后下载校验仍以该文件中对应的最终记录为准；仍标为“待验证”的项目不构成本版本已经通过的声明。
