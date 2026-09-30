# TabLink 正式版自动更新

TabLink 使用一个统一的 `stable` 发布频道，再由各平台采用系统允许的安装方式。正式清单地址为：

```text
https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json
```

Windows 与 Android 会在启动或回到前台时检查，之后每 6 小时再检查一次。浏览器客户端随 Windows 主机内置资源一起更新。iPhone、iPad 和 HarmonyOS NEXT 原生客户端读取同一份清单，但安装分别交给 App Store 和 AppGallery；普通应用不能自行静默替换这些平台上的原生代码。

## 信任与校验

发布清单采用 ECDSA P-256 / SHA-256 签名。信封只包含标准 Base64 编码的 `payload` 和 `signature`；客户端先用内置公钥验签，再解析 JSON。每个平台的条目同时固定稳定版 SemVer、构建号、HTTPS 地址、文件大小和 SHA-256。预发布版本、降级、重复平台、未知字段、HTTP 地址、错误签名和被修改的包都会被拒绝。

正式私钥只保存在当前用户的 `%LOCALAPPDATA%\TabLink\release-signing\stable-private.pem`，不进入源码、安装包、下载站或诊断文件。公钥在 `updates/stable-public-key.spki.base64`，可公开随客户端分发。丢失私钥后，现有客户端无法信任由新密钥直接签出的清单，因此应由用户另行制作安全离线备份。

发布百分比使用本机随机安装标识稳定分桶，不读取手机序列号。清单从正式频道撤回某个平台的包后，该平台会丢弃尚未安装的旧待办记录。

## 各平台行为

| 平台 | 检查与下载 | 安装行为 |
| --- | --- | --- |
| Windows x64 | 后台检查、下载并二次验签；活跃副屏期间延期 | 最后一个副屏会话停止后等待 10 秒，独立更新器自动重启安装；启动健康检查失败会恢复上一目录并重启旧版 |
| Android 直接分发 | 回到前台立即检查，空闲时后台下载；投屏期间延期 | 使用 `PackageInstaller`；满足系统条件时请求无需用户操作，系统要求确认时必须显示 Android 的安装确认页 |
| 浏览器 | 页面资源内置在 Windows 主机中，响应禁用缓存 | 下次连接或受控重连时使用更新后的主机资源 |
| iPhone / iPad | 原生客户端读取同一签名清单 | 只打开清单签名过的 App Store 地址；下载、审核和自动安装由 App Store 管理 |
| HarmonyOS NEXT | 原生客户端读取同一签名清单 | 只打开清单签名过的 AppGallery 地址；下载和自动安装由应用市场管理 |

Windows 下载包和签名清单保存在 `%LOCALAPPDATA%\TabLink\updates`。自动替换只允许从精确的 `%ProgramFiles%\TabLink` 正式安装目录执行；从 Desktop、OneDrive 或其他镜像运行时仍可检查并下载新版，但会提示从正式目录启动，且不会复制或启动提权更新器。真正执行替换的更新器四件套按内容哈希复制到 `%ProgramData%\TabLink\Updater\sha256-<包哈希>\`，解压、旧版备份和失败版本位于 `%ProgramData%\TabLink\Transactions\<事务号>\`。`Program Files`、`ProgramData`、正式安装目录和完整祖先链必须位于同一本地卷且不得包含重解析点；祖先目录允许 Windows 默认的创建权限，但拒绝普通用户删除或替换现有受保护子目录、改变 DACL 或取得所有权。正式安装树、TabLink 的 ProgramData 树和 helper 文件采用更严格的逐项策略，普通用户不能写入、删除或改变权限。受保护目录与文件由 Administrators 拥有、关闭继承，并只允许 SYSTEM 与 Administrators 写入；复制和启动前都会锁定并复核大小、SHA-256、owner 与 DACL。更新器先锁定并验签 ZIP、完成安全解压、逐文件复核 staging、验证旧安装全树 ACL，并保存旧树逐文件哈希；这些预检全部通过后才发送带 256 位随机挑战的就绪握手并让主程序退出。目录交换前会再做一轮包、staging、ACL 和旧树快照检查。新版必须在受保护事务目录中写入绑定 PID、启动时间、程序路径与随机挑战的健康信号，并继续运行至少 3 秒；失败时只有通过原快照复核的旧目录才能恢复和启动。

Android 在交给系统安装器前会重新验签保存的清单、重新计算 APK 大小和 SHA-256，并核对包名、versionName、versionCode 及当前安装签名。当前直装 APK 沿用已经安装在测试平板上的开发签名，以便原地升级并保留设置。改用新的商店正式签名之前必须设计签名轮换或执行一次卸载重装，不能把不同签名的 APK 当成可直接覆盖升级。

## 制作稳定版

先从干净目录构建完整 Windows 包与 Android APK，再取得 APK 的签名证书 SHA-256。随后运行：

```powershell
.\tools\New-TabLinkStableRelease.ps1 `
  -Version 0.8.0 `
  -WindowsBuild 800 `
  -WindowsSource .\artifacts\build-0.8.0 `
  -AndroidApk .\android\artifacts\TabLink-android-0.8.0-debug.apk `
  -AndroidBuild 11 `
  -AndroidSignerSha256 <64位证书指纹> `
  -BaseUrl 'https://linjie.space/download/api/download?path={path}' `
  -Notes 'TabLink 0.8.0 正式版自动更新'
```

工具只在 `artifacts/stable/<version>` 暂存，不会上传。它拒绝覆盖已存在的版本目录，并输出 `payload.json`、`manifest.json`、`SHA256SUMS.txt` 和 `release-summary.json`。

上传顺序必须是：

1. 把 `release` 目录内的版本包复制到下载站的 `Files\TabLink\stable\<version>`。
2. 从公网重新下载各版本包，核对大小和 SHA-256。
3. 最后把已验签的 `manifest.json` 复制为 `Files\TabLink\stable\manifest.json`。
4. 从公网读取清单，用固定公钥验签，并再次核对清单中的版本包。

同一个版本号的包不可原地替换。需要回退时，应把旧代码重新构建成更高的新版本号并发布；Windows 目录级启动失败由本机更新器自动回滚。

## 尚需各平台账号完成的工作

iOS / iPadOS 工程还没有 Apple Developer Team、App Store Connect 记录、签名归档或 App Store ID。HarmonyOS NEXT 工程还没有 DevEco SDK 编译、正式签名、HAP、AppGallery 记录或真机验收。在这些条件完成前，正式清单不会伪造 `ios` 或 `harmony` 下载条目；源码中的更新适配器只会安静地报告该平台尚未发布。
