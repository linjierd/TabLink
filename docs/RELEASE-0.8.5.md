# TabLink 0.8.5 Preview 1

发布日期：2026-10-01

0.8.5 修复了 Windows 端在目标 90 fps 时只能稳定发送约 65 fps 的主要原因。旧版在普通桌面没有持续变化时，Desktop Duplication 每帧可能额外等待半个帧周期；同时媒体 PTS 始终按名义 90 fps 序号推进，即使源帧实际更慢，也会把真实墙钟时间压缩。Android 因而频繁重新锚定时间轴，最终画面出现明显卡顿。

## 本次变化

- FFmpeg 的 `ddagrab` 在已经持有一张有效缓存帧且 `dup_frames=1` 时，使用零超时查询下一张桌面帧。若新画面已就绪就立即采集；若桌面没有变化，则立即复用缓存帧，继续由原有请求帧时钟按目标帧率输出。首次取帧、格式探测、`dup_frames=0` 和错误恢复仍保留原来的有界等待。
- Windows 媒体时间戳改为跟随单调墙钟时间。捕获源慢于目标帧率时，PTS 不再把 10 秒真实时间压缩成约 7.2 秒；编码器短暂突发时，仍保证相邻访问单元至少间隔一个目标帧周期。
- 同一个媒体时钟跨越画质切换、编码器恢复和重建继续递增，不会在同一 TCP 会话中倒退或重置。
- 新增长时间、慢源、突发、帧率切换、重建间隔和单调时钟回退测试。
- Windows 与 Android 版本同步更新到 0.8.5；Android `versionCode` 为 17。Android 的生产呈现时钟本轮保持不变。
- FFmpeg 对应源码包新增 `0002-ddagrab-nonblocking-duplicate.patch`，并继续包含完整 FFmpeg 7.0.2、nv-codec-headers、oneVPL、AMF 和 x264 对应源码及许可证。

## 实机结果

测试设备为中兴 W202DS，原生 1200 × 1920，物理面板与 Windows 虚拟副屏均为 90 Hz，Android 使用 `c2.unisoc.avc.decoder` 硬件解码。

| 场景 | Windows/Android 链路 | Android 呈现回调 | SurfaceFlinger 实际呈现 | 丢帧与背压 |
| --- | ---: | ---: | ---: | --- |
| 普通静态桌面，约 30 秒 | 89.980 fps | 89.948 fps | 86.745 fps | 0 |
| 原生 D3D11 动态源，同步约 30 秒 | 90.042 fps | 90.011 fps | 87.983 fps | 0 |

动态源自身在 40 秒内完成 9,474 次 D3D11 flip-discard Present，即 236.850 fps，足以排除测试源供帧不足。最终 `085` helper 的独立 10 秒条码复测又得到 899 个编码帧、898 个不同源 ID，按源自身时钟为 **89.746 个不同源画面/秒**。当前结果证明 Windows 捕获、发送、USB、硬件解码和回调链已经达到约 90 fps；SurfaceFlinger 仍会偶尔跨过一个 VSync，因此本预览版按实测报告约 86.7–88.0 fps 的最终呈现，不把面板 90 Hz 或编码输出帧数冒充最终可见帧率。

旧 0.8.4 在同一设备上的普通桌面发送约 65.09 fps，Android 接收约 65.28 fps。0.8.5 把这条链提升到约 90 fps，USB 每帧写入时间仍仅约 0.08 ms，不是瓶颈。

## 保持不变的边界

- 全局仍只允许一台接收设备占用一块 TabLink 虚拟副屏。连接准备真正占用副屏时才按需安装驱动；停止连接后收回并卸载自己的虚拟显示设备。
- 协议主版本仍为 v1。Wi-Fi、USB 网络共享和 ADB USB 兼容通道继续使用现有配对与设备排除规则。
- Auto 编码器仍按 NVENC、QSV、AMF 和经用户允许的 libx264 规则选择，并在一次连接内保持选定后端。
- Android 呈现预算继续使用现有 25 ms 上限。大量 `reanchor` 是时间映射保护动作，并不等于丢帧；本次测量中 `late`、`crowded`、乱序、输入、溢出、关键帧等待和背压丢帧均为 0。后续若尝试 35 ms 实验预算，会先以默认关闭的 A/B 开关验证延迟与 SurfaceFlinger 收益。
- Apple 与 HarmonyOS 原生工程本轮没有新的签名安装包。

## 安装与更新

Windows 公共 ZIP 为 self-contained x64 包。完整解压后运行 `TabLink.exe`；程序会请求管理员权限，用于按连接生命周期维护虚拟显示驱动。Android 可覆盖安装 `TabLink-Android-0.8.5-preview.1.apk`，保持现有设置和配对方式。

这是 GitHub 预览版。已签名的自动更新 `stable` 频道继续保持 0.8.0，本次发布不会让稳定版用户自动升级。最终文件哈希和完整验证记录见 [VERIFICATION-0.8.5.md](VERIFICATION-0.8.5.md) 与 GitHub Release 的 `SHA256SUMS.txt`。
