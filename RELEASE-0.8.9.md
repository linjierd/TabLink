# TabLink 0.8.9 Preview 1：可靠的线路恢复与可审查支持

<!-- tablink-version-contract: version=0.8.9; channel=preview; preview=1; androidVersionCode=21 -->

TabLink 0.8.9 Preview 1 已完成核心 W202DS 实机验收、受保护 USB 会话连续重连验收和公共预发行闭环。它把 0.8.8 发布后的线路恢复、安全清理、脱敏诊断和公开兼容性证据归入新的版本身份：Windows 为 `0.8.9`，Android 为 `0.8.9` / `versionCode 21`。保留数据覆盖安装、唯一副屏、设备实际 1920 × 1200 / 90 Hz 模式、位置变化不断线、物理呈现测量、连续两轮 Windows 会话交接、退出后的 VDD 回收、最终提交构建、精确 CI、annotated tag、GitHub prerelease 和公开回下载核验均已通过。已签名的公网 `stable` 自动更新频道在预发行后重新验签，仍保持 0.8.0。

## 受保护的 USB 会话交接

0.8.9 不再把 USB 会话 token 和端口直接交给可由其他应用启动的 Android Activity。Windows 先通过受 `android.permission.DUMP` 保护的 Provider 登记一份仅存在于 APK 进程内存、30 秒过期且只能消费一次的配置，再用 128 位随机激活标记唤醒 Activity。Activity 不接受原始 token / port extras；普通桌面启动、伪造 extras 或错误标记都不能替换会话，网络可信会话也不会被 ADB 静默抢占。

新的 Windows 会话可以明确停止仍在重试旧随机端口的 USB Session，并应用新的受保护配置。发布与激活之间会再次核对 Android 当前用户，完整事务串行执行；Provider 报错、用户切换、取消或并发启动都会失败关闭。恢复流程在这些失败中继续保留精确反向映射的清理所有权，随后只回收本会话创建的端点。

## 线路变化时如何恢复

0.8.9 对已经认证的原生网络连接使用稳定线路身份。旧绑定消失后，只有一个符合原线路身份的新绑定连续三次枚举成功且证据跨度至少八秒，才允许迁移；枚举失败、多个候选、候选改变、时钟回退、无关接口或仍有近期真实呈现都会重置证据。线路身份包含连接种类、接口标识、别名、索引、IPv4、前缀和适用时的 USB 设备标识，并与防火墙绑定保持一致。

Native、ADB 兼容、浏览器和额外原生入口共享一个进程级原子启动租约，避免两个启动动作同时占用唯一副屏。迁移请求只能认领一次；恢复失败时只重试已经确认的线路，不会从当前 UI 下拉框猜测另一条线路。资源没有完全释放时，原生网络会话保留待清理所有权，使精确重试成为可能，不会把清理失败记录成成功。

## 迁移不会延长配对二维码

线路迁移前，旧监听器上仍有效的一次性登记 token 会被原子退休。新监听器最多继承原 token 尚未用完的寿命，并同时受 UTC 与单调时钟截止约束；重试不会延长五分钟期限，已消费或已过期的 token 也不会复活。没有可信设备且没有仍有效的登记机会时，用户必须明确生成新的配对二维码。

## 脱敏支持包与反馈入口

Windows 的“检测与日志”页可以由用户主动导出支持包。保存前会显示与 ZIP 内容逐字节对应的文本预览；ZIP 只包含固定的五个条目：`manifest.json`、`compatibility.json`、`diagnostics.json`、`issue-summary.txt` 和 `README.txt`。它只从类型化白名单生成结构化字段，不读取或复制原始日志、设置、信任存储、USB 收据、显示租约、截图、序列号、网络地址、路径、token、证书或密钥，也不会自动上传。

仓库同时提供 Bug、性能、设备兼容性和功能建议表单。安全漏洞应通过 GitHub 私密 Security Advisory 报告，避免在公开 Issue 中暴露设备或认证信息。

## 人工审核的兼容性目录

`compatibility/catalog.json` 是人工审核的静态证据目录，并由严格验证器生成 Schema 和 Markdown 视图。目录拒绝未知字段、不一致的能力组合、缺少文档或提交闭环的证据、常见地址/路径/token/设备标识模式、reparse 边界逃逸以及非原子写入。它没有遥测，不会自动导入 Issue 或支持包，客户端也不会下载它。

公开兼容性目录已在发布后新增 `tlc-000002`，精确绑定 0.8.9 Preview 1、发布提交 `2a1c3aced048315e3171489fc410c6adeb2eed66` 和本轮 W202DS ADB 实机证据。该条目只声明本轮直接验证的独立桌面、原生方向、90 Hz 与单屏清理，并记录 SurfaceFlinger 物理呈现 86.966 fps；本轮留存证据没有保存 decoder 名称，因此目录明确写为 `unknown`，不借用 0.8.8 的 decoder 结果。它同时保留“需要 USB 调试”和“未做整机重启”的限制，不改写 0.8.8 历史记录，也不把未测的网络登记、撤销或线路迁移写成通过。

## 构建与版本门禁

`eng/version.json` 现在保存当前 Preview 身份。Windows 项目版本、Android `versionName`、Android `versionCode`、当前发布文档和构建产物路径必须与它一致，根构建、Android 构建和 GitHub Actions 都在执行耗时任务前检查该契约。Android 产物名和 `aapt` 身份检查继续从 Gradle 的真实版本派生。

CI 同时覆盖 Windows 两种浏览器功能配置、DriverSetup、所有 managed 测试、Android JVM 测试、`assembleDebug`、`lintDebug` 和 APK 签名验证。完整、逐项的当前结果和尚未完成的硬件边界见 [0.8.9 验证记录](VERIFICATION-0.8.9.md)。

受保护交接与 USB 恢复修复提交 `6dcc75e65c848643d200787ec9271051e3701a14` 对应的 [GitHub Actions run 36926091128](https://github.com/linjierd/TabLink/actions/runs/36926091128) 已成功完成 Windows 与 Android 两个 job。包含最终实机记录的发布提交 `2a1c3aced048315e3171489fc410c6adeb2eed66` 又由 [GitHub Actions run 36927438600](https://github.com/linjierd/TabLink/actions/runs/36927438600) 独立通过同样的两个 job；annotated tag `v0.8.9-preview.1` 精确指向该提交。

## 安卓安装兼容与帧率证据

ADB 兼容页先读取 Android 当前前台用户。安装操作把 APK 明确装入该用户；每次 USB 副屏会话再独立固定一次当前用户，让屏幕能力 provider、首次 Activity 启动与断线恢复始终使用同一个会话用户。若前台用户已经变化，恢复会在检查或重建反向通道前终止，不会静默切换到另一个用户。安装使用 Google Platform-Tools 的 `--no-streaming` 路径先完成文件传输、再交给系统包管理器提交，并且只有 ADB 返回独立的 `Success` 行才显示安装成功。这规避了部分厂商安装器在 streaming 事务中已经显示结果页、却一直不向 ADB 返回最终结果的问题；超时或含糊输出仍按失败处理，不会自动叠加第二个安装事务。每条命令继续绑定用户选中的唯一 USB 设备，并在执行前复核 Windows USB 身份、ADB 状态和设备排除规则。

物理呈现测量工具同时修复了与 `session-health.json` 的字段漂移。0.8.9 分别记录 SurfaceFlinger actual-present 物理呈现、Windows 呈现回调增量、Android 解码提交和 Android 呈现回调，后三项都不能替代物理呈现。ADB 会话健康数据使用带域分隔的设备序列号 SHA-256 绑定测量目标，测量 JSON 不再保存原始序列号；该跨 C# / PowerShell 规范和字段映射已经加入根构建与 CI 门禁。

## W202DS 核心实机结果

唯一授权的 W202DS 先从 0.8.8 / build 20 保留数据覆盖安装到 0.8.9 / build 21；加入受保护 USB 会话交接后，又以相同固定 Preview 签名原位覆盖最终代码候选。最后一次事务使用 `--no-streaming -r` 并返回独立 `Success`，首次安装时间保持不变、更新时间推进。设备回拉 APK 为 346,098 字节，SHA-256 `34DB1B9F2FD808D8BA7958F1744AA8915677F93FA7DABD820A62C86823F07C7D`；最终发布提交生成、GitHub 公开下载的 APK 与设备回拉 APK 三者逐字一致。新增的受保护 Provider 也已在安装包状态中出现。

本轮 ADB 兼容连接只创建一块 TabLink VDD 和一个 Android 视频 Surface。W202DS 实际报告横屏逻辑 1920 × 1200、原生 1200 × 1920、旋转 1/4 圈、支持 60 / 90 Hz，当前与请求均为 90 Hz；NVENC 硬件编码的请求和有效速率均为 90 fps。把副屏位置沿相邻边缘移动 100 像素并恢复时，同一会话保持连接、帧计数持续推进，VDD 与 Surface 数量始终各为 1。退出 TabLink 后约 7.205 秒内 VDD 从 1 回到 0，活动桌面从 2 回到 1，ToDesk 的虚拟显示适配器未受影响。

30.115 秒连续 SurfaceFlinger 测量得到物理 actual-present 86.9661 fps；同一最终样本的 Windows 呈现回调、Android 解码提交和 Android 呈现回调分别为 90.1825、89.9522 和 90.0041 fps。P95 / P99 / 最大间隔为 11.141 / 22.220 / 33.346 ms，覆盖缺口为 0。各指标含义与原始证据边界见 [0.8.9 验证记录](VERIFICATION-0.8.9.md)。

安装新 APK 后又连续执行两轮“启动 Windows 候选 → 收到真实呈现 → 正常退出”，两轮之间没有强停、清数据或重新安装 Android 客户端。两轮均使用 ADB、1920 × 1200 / 90 Hz，只创建一块 TabLink VDD，并在 12 秒稳定窗口中继续增加发送和呈现帧；第二轮成功接管了仍在后台重试旧端点的 Android Session。每轮退出后 Windows 进程、TabLink VDD 和该设备上的反向映射数量都回到 0。

一次性 token 实机重放、二维码轮换、逐连接挑战、活动撤销、整机重启、真实 Wi-Fi / USB 网络共享线路迁移、支持包真实 UI 和单独的 UI“停止连接”故障注入尚未完成，因此不包含在本次 Preview 的实机通过声明中；离线状态机测试不能替代这些实机边界。

## 兼容性与不变项

- 传输协议主版本仍为 v1；已登记设备仍使用固定电脑证书、Android Keystore P-256 身份和逐连接新挑战。
- 任意时刻仍只允许一个接收设备占用一块 TabLink 虚拟副屏；0.8.9 没有增加第三、第四块显示器。
- 本版没有 Android 解码器、FFmpeg helper、显示驱动或编码算法变化，也不新增帧率承诺。请求 Hz、解码提交、呈现回调和物理面板呈现仍是不同指标。
- Android 最低版本仍为 API 23。原生 Wi-Fi 与 USB 网络共享不需要 ADB；USB 调试兼容路径仍只接受固定哈希的 Google Platform-Tools r37.0.0 三件套。
- 公共 Windows 程序仍未做 Authenticode 代码签名，启动时 Windows 会显示未知发布者；随包驱动的既有签名与系统策略边界不变。
- 公开包仍不包含 Google ADB 二进制，也不包含受地域分发限制的浏览器 WebRTC 依赖；Android 原生接收不受此限制。

## 公共发布资产

[GitHub Release `v0.8.9-preview.1`](https://github.com/linjierd/TabLink/releases/tag/v0.8.9-preview.1) 已作为 prerelease 发布；`latest` 和签名 `stable` 更新频道都没有被推进。

| 资产 | 字节数 | SHA-256 |
| --- | ---: | --- |
| `TabLink-Windows-x64-0.8.9-preview.1.zip` | 97,320,509 | `4448CCA4151247F535139DBEC5A9D62331E9EB66E89491E0AEB47481D2726206` |
| `TabLink-Android-0.8.9-preview.1.apk` | 346,098 | `34DB1B9F2FD808D8BA7958F1744AA8915677F93FA7DABD820A62C86823F07C7D` |
| `TabLink-FFmpeg-7.0.2-corresponding-source.tar.gz` | 28,919,316 | `FD7977F53EDD262D55C49F200EB5F54B1B12F5FFA547770380448708D75EA6F2` |
| `SHA256SUMS.txt` | 326 | `D120A47FA25634F6F8AC33071D5039FB4339A3AE9E6D4EAEC6D9EBAD00B07EA5` |

四个文件都从 Release 的公开 HTTPS 地址下载到新的 E 盘目录后复核。前三项与 `SHA256SUMS.txt` 完全一致，校验文件本身也与发布前固定哈希一致。下载的 Windows ZIP 解压后共有 463 个文件，内部清单覆盖其余 462 项；路径、大小和 SHA-256 全部与最终提交 `2a1c3aced048315e3171489fc410c6adeb2eed66` 的 PublicRelease 相同。包内 `TabLink.exe` 为 FileVersion `0.8.9.0`、ProductVersion `0.8.9+2a1c3aced048315e3171489fc410c6adeb2eed66`，SHA-256 为 `CAA9678795270275B0EB5CA2439C6F2C00C342DA93A7FA8E58AFE8184D167C3B`；没有 PDB、Google ADB 三件套或 SIPSorcery 二进制。
