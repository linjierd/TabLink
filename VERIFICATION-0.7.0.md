# TabLink 0.7.0 验证记录

验证日期：2026-09-29。电脑端 0.7.0；Android 0.7.0 / versionCode 9。此文区分真实设备、真实 Windows 显示目标、浏览器互通和纯代码测试。

## 已验证的功能

- 策略/USB 核心 30 项、ADB 定位 19 项、USB 会话租约 14 项测试通过；保留 F50 Pro 精确序列号和 VID/PID 排除规则。
- 独立显示分配及位置规则 32 项、稳定身份 40 项、显示生命周期 39 项、驱动配置 40 项测试通过。
- 原生 TLS 测试通过，包括多端口隔离、render-submitted 与实际呈现证据隔离、错误时间戳拒绝和 60 轮连接/释放竞态。
- 浏览器传输与安全测试 31 项通过；三个独立的 DTLS/SRTP 会话、单次令牌、Origin、超时及迟到资源回收均覆盖。
- 实际 Chrome 153.0.8010.54 在隔离测试中完成本地 HTTPS/WSS、ICE、DTLS/SRTP、生产编码器 H.264 解码与 requestVideoFrameCallback。720p 横屏/30 请求和 1080×1920 竖屏/60 请求均收到实际视频呈现回调。详情见 browser/VALIDATION.md。
- 原 Android 默认编码参数的 1200×1920@90 合成源回归输出 450 帧；该项不是平板实际呈现帧率。
- Windows 当前真实签名 VDD 配置 3 个目标。独立分配、三路并行 H.264 编码、停止一块后继续采集其他目标、最终回收所有测试屏通过。电脑物理主屏保持 2560×1600、240 Hz、位置 0,0。
- 三路视频另用完整 FFmpeg 离线解码核对内容：第 60 帧分别为红、绿、蓝，关闭第一屏后第三屏继续为蓝色，预期颜色占比约 95.6%–95.8%，黑像素为 0%。第二、第三路初始首帧为黑色，后续帧恢复正常，未将首帧启动瞬态隐藏。
- 收回中间虚拟屏时 Windows 自动将剩余虚拟屏向主屏靠拢；只接受同身份非主虚拟目标的位置变化，保留分辨率、刷新率和物理屏。现场 CCD/GDI 前后差异有单独 JSON 记录。
- Android APK 已通过 ADB 安装到中兴 W202DS，并从设备回读 APK 校验 SHA-256 一致。
- UI 七页已通过应用自身控件渲染检查；配置读取失败不会被 ADB 修复覆盖，连接中可以为第二台设备选择另一条线路。

## 本次最终运行现场

18:09 启动交付的 0.7.0 电脑端，通过随包 ADB 连接已授权的 W202DS。APK 报告当前方向为横屏，画面 1920×1200；原生面板 1200×1920，支持 60 / 90 Hz，当前/请求为 90 Hz。平板截图确认全屏扩展桌面及透明 HUD，未出现控制栏。

18:10 完成 30.491 秒只读 SurfaceFlinger 测量，实际新呈现 1915 帧，平均 **62.8045 帧/秒**，采样覆盖无缺口，末尾没有停止推进。P95 呈现间隔 33.31 ms，最大 77.71 ms。这是实际缓冲呈现率，仍不代表每帧内容唯一；当前未达到稳定 90 fps。

最终目标池 3 块，其中 1 块供平板使用、2 块空闲且未启用。主屏布局未变。完整程序已复制并逐文件 SHA-256 核对到两个本机发行镜像；当前运行目录为工作区的 `dist\TabLink`。公开报告不保留个人绝对路径；旧版备份保留在工作区 `dist` 下的带时间戳目录。

## Android APK 校验

`android/TabLink.apk`：

```text
4002A86AE044CA703DCD045D7A90AB44DA948BB06FED4720F8024589589FEBA2
```

ADB 固定 Google Platform-Tools 37.0.0，三件套校验与原始 NOTICE 位于 tools/platform-tools。最终电脑文件以 SHA256SUMS.txt 为准。

## 证据边界

- 三块真实 Windows 虚拟输出与三条编码流不等于三台实体设备同时达到 90 fps。当前只有 W202DS 可做 Android 真机验证。
- Chromium 互通不代表所有 Safari、iPhone、iPad 或鸿蒙浏览器已通过；设备端仍需一次性安装并信任本机 CA。
- Apple 原生工程提供 UIKit、Network TLS、VideoToolbox、Metal 及实际呈现回报；Windows 上完成 133 项静态检查。没有 Mac/Xcode 编译、签名、IPA 或苹果真机成功证据。
- HarmonyOS NEXT 原生工程提供 ArkUI、TLS 与 AVCodec Surface；84 项纯协议断言和 29 项工程检查通过。没有官方 SDK 编译、签名、HAP 或鸿蒙真机成功证据。其进度为 render-submitted，明确不计入实际呈现帧数。
- USB 免开发者模式依赖设备 USB 网络共享/个人热点和正确 Windows 驱动，不能保证任意设备插线即连；程序不接管所有 USB 设备。
- 编码主路径需要可用 NVIDIA NVENC，多设备上限还受驱动、GPU 编码容量与网络带宽影响。

详细使用步骤和平台范围见 RELEASE-0.7.0.md、native/apple/VERIFICATION.md、native/harmony/VERIFICATION.md。
