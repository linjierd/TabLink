# TabLink 0.8.4 Preview 1 验证记录

验证日期：2026-10-01（Asia/Shanghai）

本文记录 0.8.4 Preview 1 的多编码器选择、软件回退、Android AVC 配置兼容和既有单副屏生命周期回归。文中把“编译进 helper”“合成探测通过”“真实桌面/平板通过”分开表述；前两项不能代替接收设备上的实际呈现。

## 候选版边界

- Windows 项目版本：`0.8.4`，FileVersion / ProductVersion 为 `0.8.4.0`。
- Android：`versionName 0.8.4`、`versionCode 16`（build 16）。
- 协议主版本仍为 v1；`0x20` 只增加客户端可忽略的可选编码器诊断字段。
- 任意时刻仍只允许一个 TabLink 显示租约和一块虚拟副屏。
- 公网 stable 更新清单继续指向 `0.8.0`；0.8.4 只作为 GitHub 预览版分发。
- 公共 Windows 包采用 self-contained x64，并排除 Google ADB 二进制和 SIPSorcery 浏览器组件。

## 已确认的实现与自动化边界

| 项目 | 状态 | 证据边界 |
| --- | --- | --- |
| 后端映射与选择顺序 | 通过 | 纯托管测试覆盖 Auto、NVENC、QSV、AMF、libx264 的穷尽映射，未知枚举 fail-closed，厂商优先和确定回退顺序。 |
| 连接内粘性选择 | 通过 | 选择结果是不可变会话状态；捕获恢复和自适应重建继续使用同一后端。新连接才重新探测。 |
| 编码器状态代次隔离 | 通过 | ADB、原生网络、浏览器和额外原生接收端都使用连接 generation；停止、失败或替换后，迟到回调不能覆盖新连接的界面、运行状态或 `encoder-selection.json`。 |
| 软件回退授权 | 通过 | 默认关闭；只有显式允许时 Auto 才能进入 libx264，强制 x264 同样要求授权。旧 schema 迁移和损坏设置均安全关闭软件回退。 |
| x264 速度上限 | 通过 | 软件后端有效目标最高 30 fps，限制线程并降低进程优先级；不降低虚拟显示器分辨率或刷新模式。最终 helper 在 1200 × 1920 @ 30 合成探测中测得 198.6–200.7 fps。 |
| 浏览器实际媒体节奏 | 通过 | 60 Hz 虚拟屏配合 30 fps x264 时使用 3000 RTP timestamp step；60 fps 硬件媒体使用 1500。真实 DTLS/SRTP 集成测试确认 30 fps 会话持续发送并精确释放唯一显示租约。 |
| H.264 访问单元约束 | 通过 | 四个后端禁用 B 帧；统一 bitstream filter 插入 AUD；最终 NVENC 与 x264 三帧样本均为每个访问单元恰好一个 AUD。首帧和配置变化后的第一帧要求 SPS/PPS/IDR。 |
| DDA 首帧前恢复 | 通过 | DDA 仅可在输出第一帧前对同一确证副屏改用 GDI 重试一次，并保持已选编码后端；输出任何帧后不再切换捕获路径。 |
| 启动与恢复期限 | 通过 | helper 目录读取和全部候选探测共享 30 秒预算；首次真实呈现期限 45 秒；已有呈现后的恢复期限 20 秒，且不能由振荡重复续期。 |
| 屏幕尺寸边界 | 通过 | Windows profile、编码入口和 Android AVC 配置均拒绝超过 16,000,000 像素的当前、原生或支持模式。 |
| Android AVC 解析 | 通过 | 34 项 JVM 断言覆盖缺失或非法 SPS、PPS、尺寸和帧率、像素上限，以及协议 v1 对未知未来 JSON 字段的兼容。 |
| Windows Core 回归 | 通过 | 30 项断言通过，其中包括 4096 × 4096 非法模式。 |
| 单副屏回归 | 通过 | 驱动配置 33、显示分配 30、清理 18 场景/78 断言、身份 40、生命周期 83 项全部通过；测试使用内存替身，不安装驱动或创建显示器。 |
| Windows 连接与传输 | 通过 | 浏览器 37、连接健康 13 场景/45、诊断 14 场景/94、USB lease 14、更新 17 场景/119 以及视频编码器回归全部通过。 |
| Android release 构建 | 通过 | 离线 JVM 套件、`assembleRelease`、`lintRelease` 全部成功；0.8.4 / build 16 APK 的 v1、v2 签名均通过。 |

## 最终 FFmpeg helper

两个 helper 都由 E 盘固定源码构建，采用中性 prefix，只开放 `file` / `pipe` 协议。硬件版与软件版是独立文件，许可和完整对应源码随发行包提供。

| 后端 | helper / 许可 | 当前主机结果 |
| --- | --- | --- |
| NVIDIA NVENC | `ffmpeg.exe` / LGPL-2.1-or-later | Auto 在 NVIDIA RTX 4060 Laptop GPU 上选中 NVENC；1200 × 1920 @ 90 的两次最终合成探测为 125.6–127.6 fps，达到 ≥0.90× 目标阈值。 |
| Intel QSV | `ffmpeg.exe` / LGPL-2.1-or-later | 已编译；当前主机强制初始化返回 MFX session `-9`，连接明确失败且未回退。需要 Intel GPU 电脑上的实机通过证据。 |
| AMD AMF | `ffmpeg.exe` / LGPL-2.1-or-later | 已编译；当前主机缺少 AMD `amfrt64.dll`，连接明确失败且未回退。需要 AMD GPU 电脑上的实机通过证据。 |
| libx264 | `ffmpeg-x264.exe` / GPL-2.0-or-later | 只有显式授权时可用；请求 90 fps 时有效目标固定为 30 fps，最终 1200 × 1920 合成探测为 198.6–200.7 fps，达到 ≥1.10× 阈值。 |

最终组件 SHA-256：

- `ffmpeg.exe`：`F47DA86A069F8F8EB30BCF42CE6137962691A9A1197386D262646A33D3D62659`
- `ffmpeg-x264.exe`：`B4C34236895D986C4ED452949515768348972DF1DC2FE8B85E81FEFEEA663EBE`
- `source-bundle.tar.gz`：`C59D8F6D6B5FD02505D36714967183747010EE128FA29F9D967550BFCAE08D30`

源码包含 FFmpeg 7.0.2、nv-codec-headers 12.2.72.0、oneVPL 2.11.0、AMF 1.4.35、固定 x264 源码、补丁、构建脚本、来源清单和全部适用许可。oneVPL 的 `third-party-programs.txt` 同时位于源码树和硬件 helper 旁。归档共有 9,687 个条目；审核未发现 `.git`、构建输出、二进制对象、链接、路径逃逸或个人 `Users` 路径。外部清单与包内 `SOURCE-BUNDLE-MANIFEST.json` 完全一致。

“Auto 已选择 NVENC”只证明当前 NVIDIA 主机的合成运行时探测与选择路径工作；它不证明 QSV、AMF 或其他电脑显卡通过，也不代替真实桌面捕获、传输、MediaCodec 和物理面板测量。

## 公共打包门禁

`build.ps1 -PublicRelease` 在生成可发布目录前后强制检查：

1. Git 工作树必须干净，成品可追溯到一个提交。
2. `SHA256SUMS` 必须恰好包含两个 helper 和对应源码包，并与实际内容一致。
3. 硬件 helper 只能提供 NVENC / QSV / AMF，不能启用 GPL、libx264 或网络协议；软件 helper 只能提供 libx264，不能包含硬件后端或网络协议。
4. 两个二进制、所有审计命令输出和源码包都不得包含个人 `Users` 路径。
5. 运行全部 Windows 托管测试、Android JVM 测试、`assembleRelease`、`lintRelease` 和 APK v1/v2 验签。
6. 构建 self-contained Windows x64 主程序、更新器和驱动维护工具；公共目录不得含 PDB、ADB 二进制或 SIPSorcery。
7. 通过 `dotnet TabLink.dll --self-test` 做纯回环传输自测，再为发布目录中的每个文件生成 `SHA256SUMS.txt`。
8. 构建和测试本身不得安装驱动、创建设备或连接平板；驱动配置始终只有一个输出。

## 必须保持的安全与兼容条件

- Auto 默认只选择探测通过的硬件后端；软件 x264 必须由用户明确授权。
- 强制后端失败必须明确报错，不得静默切到另一种 GPU 或软件编码器。
- 已选后端在本次连接内保持固定；安全桌面、捕获恢复和自适应计划重建不得改变它。
- x264 只限制视频有效 fps，不修改平板上报的原生尺寸、方向、面板请求 Hz 或 Windows 虚拟显示模式。
- 编码器选择和探测不得创建额外显示器；全局显示租约仍为 1。
- 任一配置的首个访问单元以及配置改变后的首个访问单元必须是带 SPS/PPS 的 IDR；不得输出 B 帧。
- `0x20` 新字段保持可选，旧 Android、Apple 或 HarmonyOS 接收端可以忽略；协议主版本不变。
- 诊断可以记录 GPU 厂商、设备、驱动和后端名称，但不得记录配对 token、设备私人序列号或用户路径。
- LGPL 硬件 helper 和 GPL x264 helper 必须保持为独立文件，公开包须附相应许可及完整对应源码。

## 尚待现场验证

| 项目 | 状态 |
| --- | --- |
| Intel QSV 在可用 Intel GPU 电脑上的初始化、目标吞吐和真实桌面 | 未验证 |
| AMD AMF 在可用 AMD GPU 电脑上的初始化、目标吞吐和真实桌面 | 未验证 |
| x264 真实桌面 1200 × 1920 @ 30 的长期 CPU 占用与持续稳定性 | 未验证 |
| W202DS 覆盖安装 0.8.4 / build 16，并分别接收硬件与软件 AVC | 待最终发行 APK 安装与连接验证 |
| 自适应码率重建时后端、TLS/TCP 和唯一副屏在真机联合运行中的连续性 | 未验证 |
| SurfaceFlinger 与物理面板实际帧率 | 未验证 |

历史 0.4.2 的 W202DS `89.702 fps` 长样本、0.8.1 的单副屏驱动生命周期和 0.8.3 的自适应测试只作为回归背景，不属于 0.8.4 多编码器的现场通过证据。
