# TabLink 0.8.5 Preview 1 验证记录

验证日期：2026-10-01（Asia/Shanghai）

本报告只记录 0.8.5 的 Windows 媒体时间戳、FFmpeg DDA 非阻塞重复帧修复、同步 Android build 17 和最终公开包。历史 0.8.4 结果保留在 `VERIFICATION-0.8.4.md`，旧标签与旧哈希不作改写。

## 修复前诊断

同一台中兴 W202DS、同一 1200 × 1920 @ 90 Hz 模式下，0.8.4 普通桌面约 30 秒的链路为：

- Windows 完成发送：65.089 fps；`SourceMove` 平均 15.280 ms/帧。
- USB/TCP `PacketWrite` 平均 0.07769 ms/帧，没有慢写或背压。
- Android 接收、提交、MediaCodec 输出：65.283 fps。
- Android 呈现回调：64.707 fps；该窗口有 17 个旧输出迟到丢弃。
- 输入队列保持 0/6；输入、溢出、等待关键帧、背压和乱序丢帧均为 0。
- 平板为 90 Hz，硬件解码器为 `c2.unisoc.avc.decoder`，热状态为 0。

这组数据把主要限制定位到 Windows `SourceMove`。旧 PTS 又以名义 90 fps 递增，慢源的真实墙钟时间被压缩，进一步增加 Android 时间映射修正。

## 代码与自动测试

`MediaTimestampClock` 使用可注入的单调微秒时钟，并满足以下不变量：

- 第一帧 PTS 为 0，之后严格递增。
- 实际捕获较慢时跟随真实 elapsed time。
- 短暂突发不会快于目标 fps 的媒体节拍。
- 跨 90 → 45 → 24 → 90 fps、编码器重建间隔和时钟回退保持连续。
- 90 fps 连续一小时、324,001 个时间戳的累计舍入漂移不超过 1 微秒。

`0002-ddagrab-nonblocking-duplicate.patch` 只改变已有缓存帧且 `dup_frames=1` 的正常路径；`request_frame` 的节拍等待仍然存在，不会形成忙循环。

最终自动测试结果：

- `TabLink.Video.Tests`：46 项通过。
- Core、显示身份、单屏生命周期、USB 转发所有权、更新、传输与 Windows 自测均由最终 `build.ps1 -PublicRelease` 串行通过。
- Android JVM、Gradle assembleRelease、lintRelease 和 APK v1/v2 签名检查通过。
- FFmpeg NVENC 合成生产/parser 测试发送 450/450 张 1200 × 1920 @ 90 帧，SPS/PPS/IDR、严格 PTS、单 AUD、无 B 帧与子进程清理均通过。

## 真机 90 Hz 验证

测试链为 TabLink 虚拟副屏 → D3D11 Desktop Duplication → NVIDIA NVENC → USB → Android MediaCodec → Surface。平板原生 1200 × 1920，活动模式 90 Hz，刷新周期 11.111111 ms。

### 普通桌面

31.640 秒 Android 窗口：

- 接收、提交、解码输出：2,847 帧，89.97996 fps。
- 呈现回调：2,846 帧，89.94836 fps。
- 输入、溢出、过期、等待关键帧、late、crowded、乱序丢帧和背压超时全部为 0。
- SurfaceFlinger：2,616 个新实际呈现 / 30.1575 秒 = 86.74456 fps；P95 11.152 ms，P99 22.211 ms，最长 33.331 ms，采样覆盖缺口 0。

### 原生动态源

同一次验证中，原生 D3D11 flip-discard 源运行 40 秒，`Success=true`，完成 9,474 次 Present，源速率 236.850402 fps；P50/P90 Present 间隔 4.1695/4.3528 ms。它证明测试源供帧充分，不用 GDI `WM_PAINT` 次数代替 DWM 实际 Present。

31.463 秒 Android 窗口：

- 接收、提交：2,833 帧，90.04246 fps。
- 解码输出、呈现回调：2,832 帧，90.01068 fps。
- 所有输入、队列、调度和背压丢帧为 0。
- SurfaceFlinger：2,658 个新实际呈现 / 30.2105 秒 = 87.98259 fps；P95 11.133 ms，P99 22.207 ms，最长 33.331 ms，采样覆盖缺口 0。

该动态运行证明当前端到端链路在持续变化画面下仍按约 90 fps 接收和解码。没有在这次端到端窗口中再次离线解码每个 H.264 画面的 16 位条码，因此不把 2,833 帧全部声明为互不重复的源图像。

最终 `7.0.2-tablink-085-hardware2` 又完成独立的 10 秒条码复测：1536 × 960 @ 90 Hz 下编码 899 帧，包含 898 个不同源 ID，按源自身 QPC 时间线为 **89.7460487 个不同源画面/秒**，只有一个重复 ID；源 D3D11 Present 为 239.291627 fps，捕获占单核 7.13%，没有 `fps` 重采样滤镜。该结果记录在 `third_party/ffmpeg-tablink/NATIVE-MOTION-VALIDATION.md`。

## Android 呈现时钟决策

静态窗口中 623 次 reanchor 全部由 518 次 ahead clamp 与 105 次 lead-floor 修正解释；动态窗口同理。两组真正的调度丢帧均为 0。`OnFrameRenderedListener` 已约 90 fps，剩余约 1–3 fps 差异位于 Surface 排程/合成附近。

本次没有直接把 25 ms 上限改为 35 ms。若继续优化，将以默认关闭、只对 90 Hz 生效的构造参数做 ABBA 测试；静态与动态各 4 × 30 秒，再各做 1 × 120 秒。采用门槛是动态 SurfaceFlinger 中位数至少 89.5 fps、漏槽稳定下降、全部丢帧与背压仍为 0，并且新增延迟不超过约 10 ms。只看到 reanchor 数减少不算通过。

## 最终二进制与公开包

- Windows FileVersion：0.8.5.0；ProductVersion 使用 `0.8.5+<构建时 Git 提交>`，最终值从公开 ZIP 内的 `TabLink.exe` 直接复核。
- Android：`versionName 0.8.5`，`versionCode 17`，包名 `com.tablink.client`。
- `ffmpeg.exe` SHA-256：`BB1FA5F2A5CC572C6A1D310F88348324EE43B84DF5A778FD0AF02D77B3C86627`。
- `ffmpeg-x264.exe` SHA-256：`6E3EA733AD40DA6D6D78C2DFC51BCCA950C3519D3304AE045316F7D55B89EDB7`。
- `source-bundle.tar.gz` SHA-256：`FD7977F53EDD262D55C49F200EB5F54B1B12F5FFA547770380448708D75EA6F2`。
- Windows ZIP 与 Android APK 的最终字节数和 SHA-256 记录在 GitHub Release 的外层 `SHA256SUMS.txt`；Windows 解压目录另有逐文件 `SHA256SUMS.txt`。它们在干净提交的公开构建完成后生成，不在源码提交中预填循环依赖值。

最终公开 ZIP 必须同时包含 0001、0002 两个补丁、完整对应源码入口、所有适用许可证和内部 `SHA256SUMS.txt`。解压复核必须确认内部哈希、文件版本、单屏限制、公开包不含 ADB 二进制与受额外地域限制的浏览器接收依赖。

## 已知边界

- SurfaceFlinger 本轮约 86.7–88.0 fps，尚未证明在所有负载下持续呈现 90.0 fps。
- Intel QSV 和 AMD AMF 已编入硬件 helper，并在当前非对应硬件上验证失败隔离；仍需在相应 Intel/AMD 电脑实测成功路径。
- 没有测量光子级端到端输入延迟，也没有验证音频、压感笔、多点触控或多接收设备。TabLink 仍只创建一块虚拟副屏。
- GitHub 0.8.5 是预览版；已签名的稳定自动更新清单继续保持 0.8.0。
