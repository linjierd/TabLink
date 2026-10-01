# TabLink 0.8.9 Preview 1 验证记录

<!-- tablink-version-contract: version=0.8.9; channel=preview; preview=1; androidVersionCode=21 -->

验证开始日期：2026-10-02。候选身份为 Windows `0.8.9`、Android `0.8.9` / `versionCode 21`，计划 tag 为 `v0.8.9-preview.1`。本文把离线源码门禁、实机结果和公共发布结果分开记录；0.8.8 的构建哈希、W202DS 帧率、线路表现或 Release 回下载结果不能替代 0.8.9 验证。

当前结论：**0.8.9 Preview 1 已完成 W202DS 核心实机候选验收，公共构建、tag、Release 与公开回下载仍待完成。** 已通过范围包括保留数据覆盖安装、唯一副屏、设备实际显示参数、90 fps 编解码链路、位置变化不断线、物理呈现测量和退出后的精确 VDD 回收。一次性 token 重放、二维码轮换、撤销、整机重启、真实线路迁移和支持包真实 UI 仍是明确未测边界，不属于本次通过声明。公开下载仍为 0.8.8 Preview 1，签名 `stable` 清单仍为 0.8.0。

## 本版验证范围

本轮重点防止以下失败：稳定线路误迁移、多个启动入口并发占用唯一副屏、迁移重试延长或复活登记 token、资源释放半失败后丢失所有权、支持包泄露原始诊断或身份信息、兼容性目录混入未经审核或可识别的数据，以及 Windows/Android/构建脚本之间的版本漂移。

## 离线源码与构建门禁

以下门禁不安装驱动、不创建虚拟显示设备、不连接手机或平板，也不证明真实 Wi-Fi、USB 网络共享、MediaCodec 或物理屏幕行为。

| 检查 | 当前结果 | 边界 |
| --- | --- | --- |
| `eng/version.json`、Windows、Android 和五份当前文档的版本契约 | 通过；23 次隔离执行为 2 个成功基线、21 个失败关闭负例 | Windows/Android 0.8.9、Android build 21、Preview 1；未知字段、版本/build/Preview/可见文案漂移、缺少文档以及 marker 缺失/重复均拒绝 |
| Windows Release（浏览器功能启用与禁用） | 两种配置均 0 warning / 0 error | 只证明编译与静态依赖闭环 |
| DriverSetup Release | 0 warning / 0 error | 不安装、不卸载驱动 |
| 全部 managed 测试项目 | 18 / 18 项目通过 | 使用 E 盘测试目录、回环或 fake 边界 |
| Android 呈现测量契约 | 36 项断言通过 | 固定测试向量核对序列号哈希绑定、health 字段映射、最终样本新鲜度、四类指标命名、原始序列号不落盘、SurfaceFlinger 枚举退出码、ADB 完全限定路径，以及 Google r37 三件套固定哈希；不调用 ADB 或 SurfaceFlinger |
| ADB APK 安装兼容 | Core 51 场景通过；覆盖当前用户安装、会话用户固定、用户切换失败关闭、`--no-streaming`、保留数据覆盖安装和独立 `Success`；USB 恢复 194 项断言验证用户变化发生在反向通道检查前或启动前都会终止，并且不会把未执行的启动记为已完成 | fake runner 只证明参数、目标复核与结果解析；W202DS 最终覆盖安装仍须单独完成 |
| 可信线路恢复状态机 | 23 场景 / 69 断言通过 | 不调用真实网络接口或显示 API |
| 脱敏支持包与诊断 | 18 场景 / 164 断言通过 | 临时文件测试，不等于真实 UI 导出 |
| 兼容性目录 | 13 场景 / 128 断言通过 | 静态资料校验；不证明设备兼容性 |
| Android JVM、`assembleDebug`、`lintDebug`、APK 签名 | 通过；debug APK 为 430,494 字节，SHA-256 `9F672878F77B642B7CC5FD2C2103D2F59174AD8C873215EE1CFA3C2E75CD19E8` | 包名 `com.tablink.client`、0.8.9 / build 21、minSdk 23、targetSdk 35；不运行真实 Android Keystore、MediaCodec 或网络迁移 |
| `git diff --check` 与 18 个候选文件的隐私扫描 | 通过 | 覆盖 15 个已跟踪修改文件和 3 个新增文件；未发现个人用户目录、项目绝对路径、真实设备序列号、MAC 或配对秘密；命中项仅为回环/文档地址、固定工具哈希和明确标注的测试向量。只覆盖仓库文本与生成内容 |

0.8.9 候选提交 `e34b2a99449884cb36b30342ab95defaacbfee5a` 对应的 GitHub Actions run 为 [`36920331236`](https://github.com/linjierd/TabLink/actions/runs/36920331236)，`windows-managed-tests` 与 `android-debug-tests` 均成功。后续实机记录提交会形成新的发布提交，因此创建 tag 前仍须等待精确最终提交对应的新 CI；这里的成功只证明该次候选提交。

本轮 Windows、managed 和 Android 日志位于 E 盘 `artifacts/v0.8.9-offline-20261002-031634/`；版本契约的隔离负例结果位于 `artifacts/tmp/v089-contract-tests/`。首次 Windows 本地构建发生在版本提交之前，回读 FileVersion 为 `0.8.9.0`，ProductVersion 中的源修订仍是变更前基线 `7049c73dd934dd4d4927797d9b04b9c09aa560d8`；该记录只证明版本字段已经生效，不能作为最终发布提交证据。最终公共构建仍必须从干净提交重新生成并核对精确 ProductVersion。

根构建入口随后以 `-SkipAndroid` 使用刚完成的 0.8.9 debug APK，在 E 盘 `artifacts/v0.8.9-package-smoke-20261002-032100/` 完成非公共打包冒烟：固定哈希 ADB 三件套只读核验、兼容性目录检查、全部 managed 门禁、Windows/DriverSetup publish 与传输 self-test 均通过。包内 `release-version.json` 为 0.8.9 / build 21，包含 `RELEASE-0.8.9.md`、`VERIFICATION-0.8.9.md`，`android/TabLink.apk` 与本轮 debug APK 的 SHA-256 一致。该目录是本地 framework-dependent 开发包，不是 self-contained 公共资产，也不满足干净提交或公开回下载门禁。

## 身份与打包

- [x] 从干净候选提交 `e34b2a99449884cb36b30342ab95defaacbfee5a` 完成完整 `-PublicRelease`；`TabLink.exe` 的 FileVersion 为 `0.8.9.0`，ProductVersion 为 `0.8.9+e34b2a99449884cb36b30342ab95defaacbfee5a`。实机记录提交后仍须从精确最终提交重建，不能把该目录直接上传。
- [x] 构建不可调试的 Android Release Preview；包名 `com.tablink.client`、`versionName 0.8.9`、`versionCode 21`、v1/v2 签名及固定签名证书 SHA-256 均通过门禁。该干净候选 APK 为 344,073 字节，SHA-256 `655CF12CDAFFA7E93ED690E1FEEA94BCA2F9B705D8957A6427AB004DBE4F9728`。
- [x] 在唯一授权的 W202DS 上从 0.8.8 / build 20 保留数据覆盖安装到 0.8.9 / build 21；ADB 返回独立 `Success`，`firstInstallTime` 保持不变，`lastUpdateTime` 推进。设备回拉 `base.apk` 为 344,073 字节且 SHA-256 与候选 APK 完全一致，因此也逐字包含已通过构建门禁的同一固定签名。
- [x] 候选 Windows 包共 463 个文件，包内清单覆盖除清单自身外的 462 项；包含对应 Android APK、许可、FFmpeg 两个 helper 及完整对应源码，没有 PDB、Google ADB 三件套或受地域限制的浏览器依赖。最终发布提交仍须重新执行同一检查。

首次真机尝试使用提交前生成的 0.8.9 Preview APK 和默认 streaming 安装路径。W202DS 厂商安装器显示了不含版本/session 身份的“安装完成”页面，但 ADB 安装客户端没有返回，包管理器回读仍为 0.8.8 / build 20；该次尝试已只终止挂起的单个 ADB 客户端，未停止共享 ADB 服务，也没有卸载或清除应用数据。因此它明确记为**未完成**，不能作为 0.8.9 安装证据。随后使用干净候选提交的 PublicRelease APK、固定哈希的 Google r37 三件套和唯一 W202DS，读取当前前台用户后只启动一次 `--no-streaming -r` 事务；厂商安装器依次显示“允许安装”和“安装”时，只确认了这一个已有事务，没有发起重试。该事务最终返回独立 `Success`，并由上面的版本、时间与回拉 APK 证据闭环。

## W202DS 基本功能与单屏生命周期

- [x] 0.8.9 ADB 兼容连接开始前 TabLink VDD 为 0；唯一 W202DS 的设备批准、Android 用户固定和屏幕参数读取完成后才出现本会话的一块 VDD。
- [x] 连接后只有一块 TabLink VDD、一个接收设备和一个 Android 视频 Surface；Windows 从一个活动桌面变为两个，没有创建第二块 TabLink 副屏。
- [x] W202DS 本轮实际报告横屏逻辑 1920 × 1200、原生 1200 × 1920、旋转 1/4 圈，支持 60 / 90 Hz，当前 90 Hz、请求 90 Hz；NVENC 硬件编码请求和有效速率均为 90 fps。
- [x] 沿相邻边缘把 Windows 副屏位置移动 100 像素时，同一 Windows 进程、同一 ADB 会话、一块 VDD 和一个 Android Surface 均保持，发送与呈现帧继续增长；随后已恢复原位置，未发生断线。
- [x] 在活动连接中请求 TabLink 自身正常退出后，程序完成 `StopAllAsync` 清理：约 7.205 秒内 TabLink 进程归零、VDD 从 1 回到 0、活动桌面从 2 回到 1；ToDesk 的虚拟显示适配器数量保持不变。
- [ ] 本轮没有单独注入准备失败或首次呈现超时，也没有单独点击 UI 的“停止连接”；这些路径由离线状态机测试覆盖，但不作为新增实机通过项。

## 配对、撤销与整机重启

- [ ] 同一登记 token 成功使用一次后，实机重放被拒绝，且拒绝发生在显示准备之前。
- [ ] 连续生成两枚二维码时，第一枚立即失效；第二枚也只有五分钟寿命且只能成功登记一次。
- [ ] 同一可信设备连续两次连接获得不同挑战；旧挑战签名不能用于新连接。
- [ ] Windows 撤销活动设备后，现有连接立即停止，保留的自动重连被拒绝，必须重新登记。
- [ ] 整台电脑重启后，电脑身份、固定证书和已登记设备仍可完成新挑战重连；不会持久化 bearer token。

## 真实线路恢复

- [ ] 同一 Wi-Fi 接口改变 IPv4 后，唯一稳定候选达到三次 / 八秒门槛再恢复；debounce 期间不停止会话或卸载 VDD。
- [ ] 同一 USB 网络设备重建网卡或接口索引变化后，精确设备身份仍受约束，歧义候选不会迁移。
- [ ] 用户从 Wi-Fi 切换到 USB 网络共享、再切回 Wi-Fi 时，通过明确选线重建；不会从 UI 当前项猜测目标。
- [ ] 恢复失败只重试已确认的 pinned route，不会悄悄迁移到另一块网卡。
- [ ] 迁移中的活跃二维码只继承原剩余寿命；旧监听 token 被拒绝，重试不延长期限，已消费或过期 token 不复活。
- [ ] 仍有近期真实呈现时不会因一次接口枚举波动触发错误迁移。

## 脱敏支持包 UI

- [ ] 在真实 Windows UI 中执行“预览 → 保存”，确认预览文本与 ZIP 五项内容逐字节一致。
- [ ] ZIP 不含原始日志、用户名和绝对路径、IP/MAC、USB/ADB/PnP/host/device 身份、配对链接/token/证书/密钥、设置/信任/显示租约/USB 收据或截图。
- [ ] 取消保存不写文件；目标已存在时，写入失败保留旧文件；测试 ZIP 检查后删除，不自动上传。

## 帧率与物理呈现

0.8.9 没有解码器、编码器或显示驱动性能改动。本版的请求刷新率、解码提交、呈现回调和物理呈现必须分别填写，不能复制 0.8.8 的约 90.1 / 90.0 回调数据，也不能把回调当作 SurfaceFlinger 或相机测得的物理帧率。

| 指标 | 0.8.9 结果 | 测量方法 |
| --- | ---: | --- |
| 平板支持 / 当前面板 Hz | 60 / 90；当前 90 | APK 显示能力与活动模式回读 |
| Windows 请求 Hz | 90 | 当前会话目标配置；NVENC 有效编码 90 fps |
| Windows 呈现回调增量 fps | 90.1825 | `session-health.measuredPresentedFps`；只表示回调增量，不是物理呈现 |
| Android 解码提交 fps | 89.9522 | `session-health.ClientSubmittedFps` |
| Android 呈现回调 fps | 90.0041 | `session-health.ClientPresentedFps` / `OnFrameRenderedListener`；不是物理呈现 |
| 物理呈现 fps | 86.9661 | `Measure-AndroidPresentation.ps1` 的 SurfaceFlinger actual-present 时间戳 |

本轮连续窗口为 30.115 秒，共观察到 2,619 次新 actual-present；P95 / P99 / 最大呈现间隔分别为 11.141 / 22.220 / 33.346 ms，估算错过 93 个垂直同步槽，环形记录覆盖缺口为 0，尾部没有停止推进。原始测量 JSON 与 latency 记录只保存在未提交的 E 盘私有验证目录；公开文档不保存原始设备序列号或其绑定值。

## 本次 Preview 发布门禁与已知边界

0.8.9 Preview 1 的硬件核心发布门禁是：保留数据覆盖安装、唯一目标与唯一副屏、设备实际方向/分辨率/刷新率、持续视频呈现、位置变化不断线、退出后精确 VDD 回收、干净提交的完整公共构建、精确提交 CI、annotated tag、四项 Release 资产及公开 HTTPS 回下载逐项一致。这些核心项通过后，可以带下面的明确限制发布 Preview。

一次性 token 实机重放、二维码轮换、逐连接挑战值、活动撤销、整机重启后的可信重连、真实 Wi-Fi / USB 网络共享线路迁移、真实支持包 UI 保存和独立的 UI“停止连接”故障注入仍保持未勾选。离线自动测试覆盖相应协议与状态机，不等于实机通过；这些项目不阻止本次明确标为 Preview 的核心预发行，也不得在发布说明中写成已验证。

## 公共发布

- [ ] 从干净的最终提交执行完整 `-PublicRelease`，不得使用 `-SkipAndroid`。
- [ ] CI 在精确发布提交上通过；annotated tag `v0.8.9-preview.1` 精确指向同一提交。
- [ ] GitHub Release 标记为 prerelease，包含 Windows ZIP、Android APK、FFmpeg 对应源码和 `SHA256SUMS.txt`。
- [ ] 记录每个公共资产的文件名、字节数和 SHA-256，并从 Release HTTPS 地址重新下载逐项比对。
- [ ] 对 Windows ZIP 内每个文件做清单复核，确认无 PDB、个人路径、私密诊断或未获许可的二进制。
- [ ] 签名 `stable` 清单重新下载并验签后仍为 0.8.0；发布 Preview 不推进稳定更新频道。
- [ ] 上述步骤全部完成后，才把 README 的下载链接从 0.8.8 切换到 0.8.9。

在身份、实机线路、撤销/重启、支持包 UI、公共构建、tag、Release 和公开回下载全部完成前，本版只能称为 **0.8.9 Preview 1 源码候选**，不能称为公共预发行完成。
