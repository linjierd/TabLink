# TabLink 0.8.7 Preview 1 验证记录

验证日期：2026-10-01

本文只记录 0.8.7 的 ProgramData 受保护 USB 清理队列、`Prepared` → `Owned` 授权、用户 SID 边界、固定哈希 ADB staging、公平重试和显示回收隔离。本轮已完成最终本地候选构建，并在唯一授权的 W202DS 上完成一次断开和一次重新连接验证；0.8.6 及更早版本的结果仅作为回归背景。

## 必须保持的安全与生命周期不变量

- 只有受保护 `%ProgramData%\TabLink\UsbReverseCleanup` 中 schema 3、结构完整的记录可以进入队列处理；其中只有 Windows 用户 SID 匹配的 `Owned` 可以进入 ADB 清理判断。跨用户 `Prepared` 可在不接触 ADB 的情况下安全封存，跨用户 `Owned` 必须保留并轮转，不能由当前用户清理。
- `Prepared` 只保留随机端点，不授权任何 ADB 删除。只有精确 `reverse --no-rebind` 明确成功后，同一不可变收据才可提升为 `Owned`。
- `%LOCALAPPDATA%` 中的旧显示/USB 状态不能被迁移或解释成管理员 ADB 删除权限或新版 watcher 的显示回收授权；每用户布局偏好可以只读迁移位置，但不携带所有权。
- ADB 兼容模式只接受官方 Windows Platform-Tools r37.0.0 固定 SHA-256 三件套，首次交互使用时复制到受保护 ProgramData staging；自动清理只能从该 staging 启动。源三件套缺失、哈希不符或成员为重解析点必须失败关闭；用户源目录中的无关文件不得复制。受保护目标必须恰好包含三件套，并拒绝额外文件、错误 ACL/owner 或重解析点。
- Wi-Fi 与普通 USB 网络共享不得因为 ADB 缺失、队列延期或 r37.0.0 未配置而失效；这两条原生网络路径不依赖 ADB。
- 每条目标 ADB 命令必须带 `-s <原批准设备>`，并重新核验设备状态、Windows USB VID/PID、当前排除规则、随机设备端点及既有 reverse 目标。
- 不得执行 `--remove-all`、隐式默认设备、`kill-server`、`start-server`、`tcpip`、冲突映射替换或对旧固定端口的猜测性删除。
- 一次处理至多四条；暂时无法完成的记录用持久化、严格递增的 sequence 移到队尾。顺序不得依赖文件系统 mtime，后续可处理条目不得被第一条离线记录永久饿死。
- 完成 tombstone 必须先于待处理记录删除落盘；任意崩溃点不得复活已经消费的删除权限。
- USB 清理失败不能阻止 watcher 分离原 `DisplayLease`、退休 marker 或卸载本次拥有的活动虚拟显示设备。
- 当前显示租约、marker 与不可变 bootstrap 必须按物理 VDD 目标全局存放在受保护 `%ProgramData%\TabLink\DisplayLeases\`，并由一个全机受保护文件锁串行所有 Windows 用户和交互会话；不得用用户/会话子目录绕过同一目标的唯一所有权。LocalAppData 只保留 `.last.json` / `last-display.json` 布局偏好，不能提供 watcher 所有权。
- 空闲、只打开配对页或等待扫码时不得保留活动 TabLink 虚拟显示设备。只有接收端通过认证并提交有效屏幕参数、连接即将占用副屏时才可按需安装；正常断开、连接准备失败、超时或 owner 异常退出后，必须移除本会话确证拥有的活动设备。签名驱动包与单输出配置可保留，不能把它们误报成仍有活动副屏。
- 显示生命周期锁、驱动操作锁与受保护显示租约锁必须保持单一获取顺序；独立维护命令也必须经过同一生命周期边界。活动连接以及“驱动准备完成到首个受保护 marker 建立”之间的窗口都不能被配置、收集或卸载命令穿越。
- 受保护显示目录中的未知文件、孤立 marker/bootstrap、缺失 marker、损坏 JSON、错误 ACL/owner 或重解析点必须失败关闭。marker 已原子替换但提交后复核失败时，恢复路径仍须保留精确 guard，不能把已落盘 marker 当成从未发布。
- 任意时刻仍只能存在一个 TabLink 显示租约和一块 TabLink 虚拟副屏。

## 自动验证

最终本地候选位于 `artifacts\v0.8.7-local-final-20261001203940`。该候选执行完整 `build.ps1` 并通过，结果如下：

- `TabLink.Core.Tests`：**46 个场景通过**。
- `TabLink.AdbLocator.Tests`：**22 个场景通过**。
- `TabLink.Browser.Tests`：**37 个场景通过**。
- `TabLink.DriverConfiguration.Tests`：**89 个场景通过**。
- `TabLink.DisplayLifecycle.Tests`：**192 个场景通过**。
- `TabLink.DisplayCleanup.Tests`：**22 个场景、96 条断言通过**。
- `TabLink.DisplayIdentity.Tests`：**40 个场景通过**。
- `TabLink.ConnectionHealth.Tests`：**13 个场景、45 条断言通过**。
- `TabLink.Diagnostics.Tests`：**14 个场景、94 条断言通过**。
- `TabLink.UsbLease.Tests`：**48 个场景通过**。
- `TabLink.UsbRecovery.Tests`：**135 个场景通过**。
- `TabLink.Update.Tests`：**17 个场景、119 条断言通过**。
- `TabLink.Transport.Tests` 在发布预检暴露一次临时端口释放等待后完成精确端点修复；修复后的完整套件共重复通过 **6 次**。每次都包含 **64 轮** `listenPort:0` 阻塞 accept 的精确端点唤醒与无残留监听检查，以及既有 **60 轮**接受/认证/释放竞态检查。
- 其余 `build.ps1` 纳入的传输及 Windows 回归测试全部通过。
- Windows 与 DriverSetup Release 编译：**0 个警告、0 个错误**。
- Android JVM 测试和 lint 通过；debug APK 同时通过 **APK Signature Scheme v1 与 v2** 签名验证。
- 候选 APK：`versionName 0.8.7`、`versionCode 19`，SHA-256 为 `58B6B8EEB2C37261A22B8CD4F9761D6A0E50C1F63D5488F36DDF8576F54093DD`。

本节结果属于本地最终候选；`build.ps1 -PublicRelease`、公开 ZIP/Release 资产及其发布后下载校验仍须在发布提交上单独完成。

本轮离线套件使用 fake ADB、fake USB inventory、临时 E 盘测试目录和注入进程身份，不访问真实平板、手机或虚拟显示驱动；其覆盖边界如下：

1. 两个连续会话各自留下的收据不会互相覆盖。
2. `Prepared` 在绑定前崩溃、绑定失败、绑定成功但提升前崩溃时均不会获得删除权限。
3. `Owned` 只有在精确设备、USB 身份、排除规则和映射目标仍一致时才执行单端点删除。
4. PID 复用、启动时间变化、序列号/VID/PID 变化、设备未授权、映射冲突和畸形 ADB 输出均失败关闭；SID 不匹配时，`Prepared` 无 ADB 安全封存，`Owned` 无 ADB 保留并轮转。
5. tombstone 写入后、待处理文件删除前崩溃不会再次执行删除。
6. 队列满、单文件过大、重复 JSON 属性、文件名/内部 GUID 不一致、额外字段、重解析点和不安全 ACL 均被拒绝。
7. 第一条持续 defer 后，后续记录在有界轮次内得到处理。
8. 同进程主动停止允许精确注销自己的 owner；仅凭磁盘记录不能绕过仍存活 owner 检查。
9. 固定哈希 ADB 三件套 staging 拒绝源文件缺失、篡改和来源重解析点；无关源文件不复制，受保护目标中的额外文件或用户可写权限必须被拒绝。
10. watcher 在 USB 队列处理失败时仍完成精确显示和驱动回收。
11. current、marker 与 bootstrap 只从受保护 ProgramData 读取；LocalAppData 布局偏好可恢复位置，但不能提升成显示或 USB 所有权。
12. 空闲/配对等待不创建设备，认证后的首个连接只建立一个设备；正常断开、准备失败和 owner 异常退出均精确移除该活动设备，随后下一个连接可重新按需建立。
13. 独立 `prepare`、`remove`、`configure`、`pool` 与 `collect` 命令遵守同一生命周期锁顺序，在活动连接和 prepare→marker 窗口内有界等待或失败，不发生死锁，也不能短暂绕过租约检查。
14. 受保护显示状态出现未知/孤立/损坏文件时维护命令失败关闭；原子写已经提交、随后复核注入失败时，启动回滚仍保留并执行精确 guard。
15. 本地传输服务器使用系统分配或自定义端口时，释放路径只唤醒 `Start()` 后保存的实际绑定端点；测试在 accept 已确定提交后才开始释放，并要求两秒内结束且不残留监听，避免退回固定 27183 后触发 Windows `AcceptEx`/停止竞态。

## W202DS 实机验证

本轮只对当时唯一授权的 W202DS 执行实机操作；没有向排除列表中的 F50 Pro 或其他设备发送命令，也未记录或公开设备序列号。

- 平板已安装并由包管理器确认 `versionName 0.8.7`、`versionCode 19`。
- 客户端报告逻辑 profile 为 **1920 × 1200、rotation 1**；原生面板身份为 **1200 × 1920**。本次测量时 Android 逻辑方向为横向，因此应用按实际逻辑方向请求 1920 × 1200，而不是把原生竖向面板尺寸误当成当前显示方向。
- 客户端报告 active/request/max 均为 **90 Hz**，并报告 **2 个显示 mode**。
- 第一次连接后核对为恰好 **1 块 `Root\MttVDD`、1 条 ADB reverse 映射和 1 个监听器**。
- Android 使用硬件解码器 `c2.unisoc.avc.decoder`，配置目标为 **90 fps**。第一次连接 HUD 测得 **90.9 fps submitted、90.6 fps presented**，pacing actual 为 **90.006 fps**；解码器 fallback 次数为 **0**。
- 正常断开后，TabLink 进程、`Root\MttVDD` 和 ADB reverse 映射均核对为 **0**。
- 随后重新连接成功恢复为 **1 块 VDD、1 条 reverse 和 1 个监听器**，第二次连接测得 **90.55 fps**，解码器 fallback 仍为 **0**。

本轮证据确认了 W202DS 上的正常连接、90 Hz/约 90 fps 呈现、正常断开清理及重新连接恢复。平板离线时的持久化 `Owned` 队列、公平轮转和崩溃点由自动测试覆盖，本轮没有把这些离线注入结果表述为新增的物理设备故障注入结论。

## 最终二进制与公开包

- 本地最终候选 Windows FileVersion 已确认是 `0.8.7.0`；该候选在未提交工作树上构建，正式公共包的 ProductVersion 仍须确认为 `0.8.7+<发布提交>`。
- 本地最终候选 Android APK 及 W202DS 设备包管理器均已确认：`versionName 0.8.7`、`versionCode 19`、包名 `com.tablink.client`；候选 APK SHA-256 为 `58B6B8EEB2C37261A22B8CD4F9761D6A0E50C1F63D5488F36DDF8576F54093DD`。
- 2026-10-01 已从正式 HTTPS 地址重新下载并用仓库固定公钥验签公网 `stable` 清单：releaseId 为 `tablink-0.8.0`，Windows 为 `0.8.0` / build 800，Android 为 `0.8.0` / build 11。发布本预览仍不得推进该清单。
- Windows ZIP、Android APK、FFmpeg 对应源码包和外层 `SHA256SUMS.txt` 的字节数及 SHA-256：**待干净发布提交构建后填写或由 GitHub Release 资产清单记录**。
- Git commit、tag、GitHub Actions 与 Release URL：**待最终发布后填写**。

## 尚未关闭的边界

- ADB 不提供原子 compare-and-delete；本版在删除前第二次核验精确映射，但另一个本机管理员进程在最终核验后主动竞态仍超出普通防护边界。
- 0.8.6 及更早版本位于 `%LOCALAPPDATA%` 的收据不被信任，升级前已存在的旧 reverse 映射不会由 0.8.7 自动删除。
- 公共 ZIP 不含 Platform-Tools；用户未成功 staging 固定哈希 r37 三件套前，队列保留且自动清理延期。成功 staging 一次后，后台可复核并复用受保护副本。
- 长期可信设备配对、网络发现和跨 IP 自动重连尚未实现。
- Intel QSV、AMD AMF、Apple 原生客户端和 HarmonyOS 原生客户端本轮尚无新的实机结果。
