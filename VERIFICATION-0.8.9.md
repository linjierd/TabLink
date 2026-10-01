# TabLink 0.8.9 Preview 1 验证记录

<!-- tablink-version-contract: version=0.8.9; channel=preview; preview=1; androidVersionCode=21 -->

验证开始日期：2026-10-02。候选身份为 Windows `0.8.9`、Android `0.8.9` / `versionCode 21`，计划 tag 为 `v0.8.9-preview.1`。本文把离线源码门禁、实机结果和公共发布结果分开记录；0.8.8 的构建哈希、W202DS 帧率、线路表现或 Release 回下载结果不能替代 0.8.9 验证。

当前结论：**0.8.9 Preview 1 源码候选，尚未完成实机与公共发布验收。** 公开下载仍为 0.8.8 Preview 1，签名 `stable` 清单仍为 0.8.0。

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

最近一次 0.8.8 后开发基线的 GitHub Actions run 为 [`36911542956`](https://github.com/linjierd/TabLink/actions/runs/36911542956)，Windows 与 Android 两个 job 均成功。它发生在版本提升之前，只能作为变更基线，不能证明 0.8.9 身份或产物。

本轮 Windows、managed 和 Android 日志位于 E 盘 `artifacts/v0.8.9-offline-20261002-031634/`；版本契约的隔离负例结果位于 `artifacts/tmp/v089-contract-tests/`。首次 Windows 本地构建发生在版本提交之前，回读 FileVersion 为 `0.8.9.0`，ProductVersion 中的源修订仍是变更前基线 `7049c73dd934dd4d4927797d9b04b9c09aa560d8`；该记录只证明版本字段已经生效，不能作为最终发布提交证据。最终公共构建仍必须从干净提交重新生成并核对精确 ProductVersion。

根构建入口随后以 `-SkipAndroid` 使用刚完成的 0.8.9 debug APK，在 E 盘 `artifacts/v0.8.9-package-smoke-20261002-032100/` 完成非公共打包冒烟：固定哈希 ADB 三件套只读核验、兼容性目录检查、全部 managed 门禁、Windows/DriverSetup publish 与传输 self-test 均通过。包内 `release-version.json` 为 0.8.9 / build 21，包含 `RELEASE-0.8.9.md`、`VERIFICATION-0.8.9.md`，`android/TabLink.apk` 与本轮 debug APK 的 SHA-256 一致。该目录是本地 framework-dependent 开发包，不是 self-contained 公共资产，也不满足干净提交或公开回下载门禁。

## 身份与打包

- [ ] 从干净候选提交构建 Windows，核对 `TabLink.exe` 的 FileVersion 为 `0.8.9.0`，ProductVersion 含 `0.8.9` 和精确源提交。
- [x] 本地构建不可调试的 Android Release Preview；包名 `com.tablink.client`、`versionName 0.8.9`、`versionCode 21`、v1/v2 签名及固定签名证书 SHA-256 均通过门禁。该本地 APK 为 344,039 字节，SHA-256 `B7C0B31EA66F4A4F7C03D528BF419DF8AEB656A721771DD6D145FE370A2243F1`；它不是最终 GitHub 资产，提交后必须重建。
- [ ] 在 W202DS 上从 0.8.8 覆盖安装，并从设备回读包名、版本、build、签名和候选 APK 哈希。
- [ ] 完整 Windows 包包含对应 Android APK、许可、FFmpeg 两个 helper 及完整对应源码；公共包不包含 Google ADB 或受地域限制的浏览器依赖。

首次真机尝试使用提交前生成的 0.8.9 Preview APK 和默认 streaming 安装路径。W202DS 厂商安装器显示了不含版本/session 身份的“安装完成”页面，但 ADB 安装客户端没有返回，包管理器回读仍为 0.8.8 / build 20；该次尝试已只终止挂起的单个 ADB 客户端，未停止共享 ADB 服务，也没有卸载或清除应用数据。因此它明确记为**未完成**，不能作为 0.8.9 安装证据。后续只使用最终干净提交的 PublicRelease APK，通过唯一 W202DS 读取当前前台用户并执行一次对应用户的 `--no-streaming -r` 安装；只有 ADB 独立成功、设备版本/build、原安装时间、回拉 APK 哈希和固定签名全部匹配后才勾选本项。

## W202DS 基本功能与单屏生命周期

- [ ] 0.8.9 完成首次登记或既有可信身份重连，认证及屏幕参数完成前没有显示驱动变更。
- [ ] 连接后只有一块 TabLink VDD 和一个接收设备；使用平板当前方向与真实上报模式。
- [ ] 核对 1920 × 1200 逻辑尺寸、原生 1200 × 1920、面板 90 Hz / 请求 90 Hz；若设备状态不同，按实际值记录。
- [ ] 正常停止、准备失败、超时和应用退出只回收本会话精确拥有的显示，并在停止后卸载 TabLink 虚拟显示设备。
- [ ] 更改 Windows 显示器相对位置不应断开已经认证的会话。

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
| 平板支持 / 当前面板 Hz | 未测 | 待设备只读能力与当前模式回读 |
| Windows 请求 Hz | 未测 | 待当前会话配置记录 |
| Windows 呈现回调增量 fps | 未测 | `session-health.measuredPresentedFps`；只表示回调增量，不是物理呈现 |
| Android 解码提交 fps | 未测 | `session-health.ClientSubmittedFps` |
| Android 呈现回调 fps | 未测 | `session-health.ClientPresentedFps` / `OnFrameRenderedListener`；不是物理呈现 |
| 物理呈现 fps | 未测 | `Measure-AndroidPresentation.ps1` 的 SurfaceFlinger actual-present 时间戳；若不测则保持“未测” |

## 公共发布

- [ ] 从干净的最终提交执行完整 `-PublicRelease`，不得使用 `-SkipAndroid`。
- [ ] CI 在精确发布提交上通过；annotated tag `v0.8.9-preview.1` 精确指向同一提交。
- [ ] GitHub Release 标记为 prerelease，包含 Windows ZIP、Android APK、FFmpeg 对应源码和 `SHA256SUMS.txt`。
- [ ] 记录每个公共资产的文件名、字节数和 SHA-256，并从 Release HTTPS 地址重新下载逐项比对。
- [ ] 对 Windows ZIP 内每个文件做清单复核，确认无 PDB、个人路径、私密诊断或未获许可的二进制。
- [ ] 签名 `stable` 清单重新下载并验签后仍为 0.8.0；发布 Preview 不推进稳定更新频道。
- [ ] 上述步骤全部完成后，才把 README 的下载链接从 0.8.8 切换到 0.8.9。

在身份、实机线路、撤销/重启、支持包 UI、公共构建、tag、Release 和公开回下载全部完成前，本版只能称为 **0.8.9 Preview 1 源码候选**，不能称为公共预发行完成。
