# TabLink 0.8.9 Preview 1：可靠的线路恢复与可审查支持

<!-- tablink-version-contract: version=0.8.9; channel=preview; preview=1; androidVersionCode=21 -->

TabLink 0.8.9 Preview 1 当前是源码候选。它把 0.8.8 发布后的线路恢复、安全清理、脱敏诊断和公开兼容性证据归入新的版本身份：Windows 为 `0.8.9`，Android 为 `0.8.9` / `versionCode 21`。在候选完成实机、公共构建、tag、Release 和公开回下载核验前，GitHub 上可下载的最新预览仍是 0.8.8 Preview 1；已签名的公网 `stable` 自动更新频道仍保持 0.8.0。

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

当前目录中的 W202DS 记录仍精确绑定 0.8.8 Preview 1。只有 0.8.9 完成新的实机验证后，才会以新记录 ID 添加 0.8.9 证据；不会改写旧记录。

## 构建与版本门禁

`eng/version.json` 现在保存当前 Preview 身份。Windows 项目版本、Android `versionName`、Android `versionCode`、当前发布文档和构建产物路径必须与它一致，根构建、Android 构建和 GitHub Actions 都在执行耗时任务前检查该契约。Android 产物名和 `aapt` 身份检查继续从 Gradle 的真实版本派生。

CI 同时覆盖 Windows 两种浏览器功能配置、DriverSetup、所有 managed 测试、Android JVM 测试、`assembleDebug`、`lintDebug` 和 APK 签名验证。完整、逐项的当前结果和尚未完成的硬件边界见 [0.8.9 验证记录](VERIFICATION-0.8.9.md)。

## 兼容性与不变项

- 传输协议主版本仍为 v1；已登记设备仍使用固定电脑证书、Android Keystore P-256 身份和逐连接新挑战。
- 任意时刻仍只允许一个接收设备占用一块 TabLink 虚拟副屏；0.8.9 没有增加第三、第四块显示器。
- 本版没有 Android 解码器、FFmpeg helper、显示驱动或编码算法变化，也不新增帧率承诺。请求 Hz、解码提交、呈现回调和物理面板呈现仍是不同指标。
- Android 最低版本仍为 API 23。原生 Wi-Fi 与 USB 网络共享不需要 ADB；USB 调试兼容路径仍只接受固定哈希的 Google Platform-Tools r37.0.0 三件套。
- 公共 Windows 程序仍未做 Authenticode 代码签名，启动时 Windows 会显示未知发布者；随包驱动的既有签名与系统策略边界不变。
- 公开包仍不包含 Google ADB 二进制，也不包含受地域分发限制的浏览器 WebRTC 依赖；Android 原生接收不受此限制。

## 发布资产状态

计划中的公共资产名为：

- `TabLink-Windows-x64-0.8.9-preview.1.zip`
- `TabLink-Android-0.8.9-preview.1.apk`
- `TabLink-FFmpeg-7.0.2-corresponding-source.tar.gz`
- `SHA256SUMS.txt`

这些文件目前尚未作为 0.8.9 Release 发布，因此本文件不预填 tag、字节数或 SHA-256。只有从干净的最终提交完成 `-PublicRelease`、核对 Windows/APK 身份、创建精确指向该提交的 `v0.8.9-preview.1` annotated tag、发布 prerelease，并从公开 HTTPS 地址重新下载逐项比对后，才会记录最终值并把 README 下载入口切换到 0.8.9。
