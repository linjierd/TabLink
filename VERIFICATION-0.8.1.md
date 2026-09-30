# TabLink 0.8.1 验证记录

验证日期：2026-09-30（Asia/Shanghai）

本文件只记录 0.8.1 Windows 单副屏与按需驱动生命周期的证据。Android、Apple 和 HarmonyOS NEXT 客户端没有在本候选版升版，0.8.0 的既有验证不能替代本次 Windows 生命周期验证。

## 候选版边界

- Windows `TabLink.exe` 的项目版本设为 `0.8.1`。
- Android 交付仍使用 `android/artifacts/TabLink-android-0.8.0-debug.apk`。
- 构建脚本不生成或发布公网更新清单，也不调用驱动安装命令。
- 最终输出必须来自一个新建或空的目录；`SHA256SUMS.txt` 只在全部测试、自测和复制完成后生成。

## 自动化回归范围

根目录 `build.ps1` 按顺序执行以下与本次改动直接相关的纯软件测试，避免多个 `dotnet` 进程并发写入共享 `obj`：

1. `TabLink.DriverConfiguration.Tests`：输出数只允许 1、旧多输出配置收敛、模式变换和幂等。
2. `TabLink.DisplayAllocation.Tests`：全局只允许一个显示目标，第二个分配在驱动操作前被拒绝。
3. `TabLink.DisplayCleanup.Tests`：连接准备、失败回滚、释放顺序和按需驱动控制器场景。
4. `TabLink.DisplayLifecycle.Tests`：显示身份、守护和收回生命周期回归。
5. `TabLink.Diagnostics.Tests`、`TabLink.Update.Tests`、Core 测试和发布目录中的 `--self-test`：确认诊断、更新及传输既有行为没有回归。

这些测试不得安装、重新加载或删除真实显示设备。测试使用模拟控制器、临时文件或纯逻辑路径；真实设备操作只允许在最终候选构建完成后单独进行。

2026-09-30 在共享工作区完成的定向回归结果如下，命令均以 Release 配置串行运行并返回退出码 0：

| 测试项目 | 结果 |
| --- | --- |
| `TabLink.DisplayCleanup.Tests` | 18 个场景、78 项断言通过 |
| `TabLink.DisplayAllocation.Tests` | 30 项断言通过 |
| `TabLink.DriverConfiguration.Tests` | 33 项断言通过 |
| `TabLink.Windows` Release build | 0 warning、0 error |
| `TabLink.DriverSetup` Release build | 0 warning、0 error |

这些结果证明定向逻辑和编译在当时的共享源码上通过；它们不等于完整 `build.ps1` 发布构建，也不等于真实设备安装、连接和断开验证。

## 源码验收点

- 所有传输入口共用同一个显示分配上限，不能各自再取得一块副屏。
- 驱动配置接口拒绝大于 1 的输出数；旧版 `count=3` 或 `count=4` 在设备安装前变为 `count=1`。
- 应用启动、监听网络和生成二维码不触发安装；设备认证与屏幕参数有效后才进入准备流程。
- 若已有唯一且合规的 TabLink 活动设备，准备流程复用并核验它；若不存在，先写入单输出配置再创建设备。
- 任一准备步骤失败或取消时释放全局租约，并清理本次流程创建的活动设备。
- 停止连接先结束捕获、输入和传输，再释放显示守护，最后移除活动设备；没有其他租约时才允许移除。
- 移除只针对经过所有权核验的精确 TabLink 设备节点，保留 Driver Store 驱动包和配置；不得调用 `/delete-driver`。
- 同类设备数量异常或所有权无法确认时失败关闭，不按友好名称或模糊硬件名称批量删除。

## 最终本机构建记录

最终集成使用新的空目录执行；构建脚本在发布之前串行运行 Core、ADB 定位、浏览器、驱动配置、显示分配、显示回收、显示身份、显示生命周期、诊断、原生传输、USB lease、视频和稳定更新测试：

```powershell
.\build.ps1 -SkipAndroid -OutputDirectory .\artifacts\build-0.8.1-single-display-final-20260930-0156
```

2026-09-30 01:53（Asia/Shanghai）的完整集成构建返回退出码 0，结果如下：

| 项目 | 结果 | 证据 |
| --- | --- | --- |
| 全部串行自动化测试 | 通过 | Core 30；ADB Locator 19；Browser 31；Driver Configuration 33；Display Allocation 30；Display Cleanup 18 场景/78 断言；Display Identity 40；Display Lifecycle 82；Diagnostics 14 场景/94 断言；USB Lease 14；Update 17 场景/119 断言；原生传输和视频测试全部通过 |
| Windows publish | 通过 | 生成 `TabLink.exe`、`TabLink.DriverSetup.exe`，发布目录共 156 个文件 |
| `TabLink.exe` ProductVersion | 通过 | `0.8.1`（FileVersion `0.8.1.0`） |
| Windows 纯传输自测 | 通过 | `selftest-result.txt` 共 21 项 PASS |
| 完整制品哈希 | 通过 | `SHA256SUMS.txt` 28 项逐项重新计算，0 缺失、0 不匹配；本次候选 `TabLink.exe` SHA-256 为 `35BC6DD719E3B50C98C836E4AEE4C5F52332C9B5A43E77089B1FAC7D21682A22` |
| 0.8.1 文档入包 | 通过 | `RELEASE-0.8.1.md`、本文件均存在 |
| 构建前后活动 MttVDD | 通过 | 均为 0；构建没有安装、重启或卸载真实显示设备 |

构建时电脑上没有 `TabLink*` 进程，没有活动 `Root\MttVDD` 节点，也没有 protected device receipt。`C:\VirtualDisplayDriver` 和 Driver Store 中的既有驱动包仍保留；它们本身不会创建活动显示输出。

## 本机部署记录

2026-09-30 02:00（Asia/Shanghai）将上述最终构建部署到本机发行目录。部署先复制到同目录的独立 staging 文件夹，验证 ProductVersion 和全部 28 项制品哈希后，才把旧目录整体移动为带时间戳的备份，再切换 staging 目录。部署后的结果为：

- `TabLink.exe` ProductVersion `0.8.1`、FileVersion `0.8.1.0`。
- 发布目录 156 个文件；`SHA256SUMS.txt` 逐项复核 0 个失败。
- `TabLink.exe` SHA-256：`35BC6DD719E3B50C98C836E4AEE4C5F52332C9B5A43E77089B1FAC7D21682A22`。
- 部署后活动 MttVDD 为 0、运行中的 `TabLink*` 进程为 0、protected device receipt 不存在；部署没有安装或启动虚拟显示设备。

## 最终现场生命周期检查

2026-09-30 02:04 至 02:07（Asia/Shanghai），在中兴 W202DS 平板上完成 0.8.1 的真实 USB 生命周期检查；公开报告已移除真实设备序列号。检查从活动 MttVDD 为 0 的空闲状态开始，断开后也恢复到同一状态。汇总证据保存在未提交的本机验证目录中。

| 验收项 | 现场结果 | 证据 |
| --- | --- | --- |
| 按需安装唯一设备 | 通过 | 连接前 MttVDD 为 0；连接准备结果为 `singleDisplayReady`，只创建一个受所有权核验的显示设备，无需重启；公开报告已移除本机实例号 |
| 单副屏上限 | 通过 | 连接期间 PnP 计数为 1；实时显示探针也只列出一个目标，状态为 active、non-primary、non-cloned；公开报告已移除本机显示目标编号 |
| 独立扩展桌面 | 通过 | 租约记录为 1920 × 1200、90 Hz，主屏仍为 2560 × 1600、240 Hz；平板截图显示独立 Windows 桌面 |
| 平板能力读取和传输 | 通过 | APK profile 报告原生 1200 × 1920、支持 60/90 Hz；本次横向流为 1920 × 1200、90 Hz；平板叠加层实测 H.264 解码 65.6 fps、屏幕 90 Hz、请求 90 Hz |
| 正常断开回收 | 通过 | 02:06:11 先成功收回精确 CCD 目标，再返回 `notPresent`；最终 MttVDD 为 0、`TabLink*` 进程为 0、protected ownership receipt 不存在 |
| 保留包后重连 | 通过 | 02:04:37 首次断开已卸载活动设备，随后同一平板再次连接并创建唯一设备；最终再次卸载。Driver Store 仍保留已签名驱动包，配置仍为 `count=1`；公开报告已移除本机 OEM 编号 |
| 物理主屏保持 | 通过 | 断开后的 session-health 只列出原物理主屏 `\\.\DISPLAY1`（2560 × 1600）；显示停止结果明确只收回本次 TabLink CCD 目标 |

本次现场运行没有再接入第二台物理接收设备。第二个并发请求在驱动操作前被拒绝这一项由 Display Allocation、Display Cleanup、Browser 和 Display Lifecycle 自动化测试覆盖；现场 PnP 与显示目标采样均只观察到一个 MttVDD 和一个 TabLink 显示目标。整机断电、系统崩溃和强制同时终止所有 TabLink 相关进程没有在本次现场检查中模拟；下一次连接仍会通过受保护回执和精确实例核验处理可证明属于 TabLink 的残留。

截图中的 65.6 fps 和“屏幕 90 Hz / 请求 90 Hz”是该时刻的应用叠加状态，证明本次会话按 90 Hz 模式请求并显示；它不代表长时间稳定保持 90 fps，也不是外部仪器对面板刷新率的测量。现场仅核对了 TabLink 精确目标和原物理主屏，未对 ToDesk、向日葵等第三方虚拟显示驱动做完整的前后事件审计。

连接的 Android 客户端仍为 0.7.1。它已完成本次 Windows 0.8.1 的实际连接、90 Hz 传输和断开卸载检查；此前尝试的 0.8.0 原地安装没有在该平板的厂商安装器中提交，因此本记录不声称 Android 已升级到 0.8.0。

本次没有执行公网发布。
