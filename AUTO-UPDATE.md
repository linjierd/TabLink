# TabLink 正式版自动更新

TabLink 使用一个统一的 `stable` 发布频道，再由各平台采用系统允许的安装方式。客户端内置两个正式清单地址，个人博客为主地址，GitHub 的最新**正式版** Release 为备用地址：

```text
https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json
https://github.com/linjierd/TabLink/releases/latest/download/manifest.json
```

主地址不可达、返回无效内容或未通过签名验证时，客户端可以从备用地址取得正式清单；两个来源都有效时采用 `publishedAtUtc` 较新的已签名决定。同一发布时间若版本、构建号、包大小、SHA-256、发布比例或协议门槛冲突，则失败关闭，并持久记录该冲突时间；本轮、后续检查和重启后都拒绝该时间及更早的普通决定，直到取得发布时间更晚且有效的签名决定。较新的已签名暂停也具有权威性，不能被另一来源中的旧版本绕过。传输地址本身不构成信任：从博客或 GitHub 取得的清单都必须通过同一枚内置公钥的签名验证，下载包仍须同时匹配已签名的大小与 SHA-256。GitHub 备用地址只跟随非 prerelease 的 `latest` Release；Preview / prerelease 不得附着或推进正式清单，也不得因此进入稳定更新路径。

Windows、Android、iPhone / iPad 与 HarmonyOS NEXT 原生客户端都会持久保存本机已经接受的最高 `publishedAtUtc` 和不含下载 URL 的发布决定摘要。后续清单早于这个高水位，或在同一时间给出不同版本、哈希、发布比例、协议门槛、安装地址或说明时会失败关闭；仅下载 URL 不同且包身份完全一致时才视为博客与 GitHub 的等价镜像。这样，设备重启或暂时只访问到一个来源时，也不会回放较旧的已签名清单来绕过已经看到的暂停决定。

截至 2026-10-02，博客主地址返回 HTTP 530，GitHub 备用地址因公开 Release 仍全部是 Preview 而返回 404。在任一来源发布可用的已签名 stable 清单之前，端到端自动更新不可用；客户端会失败关闭，且不会用 Preview 资产或未签名内容顶替 stable 清单。任一来源恢复后，另一个来源不可达不会阻止客户端继续验证并使用可用来源。

Windows、Android、iPhone / iPad 与 HarmonyOS NEXT 原生客户端的“设置”都提供三种更新策略：

| 设置 | 检查与下载 | 安装 |
| --- | --- | --- |
| **自动更新（Automatic）** | 启动或回到前台时检查，之后定时检查；Windows / Android 空闲时自动下载并校验，Apple / HarmonyOS 只准备已验签的商店版本信息 | Windows 在无副屏会话时自动安装；Android 再次确认最新签名决定后直接交给系统安装器，不增加应用内确认，但仍保留 Android 系统确认；Apple / HarmonyOS 的下载与自动安装由应用市场和系统设置管理 |
| **自动下载后手动安装（DownloadThenAsk）** | 自动检查；Windows / Android 后台下载已验签的正式版，Apple / HarmonyOS 自动验证商店更新信息但不能由普通应用预下载商店安装包 | 用户必须在设置或更新入口明确选择安装或打开应用市场；Android 随后仍显示系统确认 |
| **从不更新（Never）** | 不发起后台更新请求，也不下载 | 不自动安装，更新安装入口停用；已校验缓存可保留，但不会绕过当前设置执行 |

首次没有保存设置时使用“自动更新”。损坏的更新偏好会失败关闭为“从不更新”，而不是继续联网或安装。切换策略后立即生效，不需要断开正在使用的副屏。浏览器客户端随 Windows 主机内置资源一起更新。iPhone、iPad 和 HarmonyOS NEXT 原生客户端读取同一签名频道，但安装分别交给 App Store 和 AppGallery；普通应用不能自行静默替换这些平台上的原生代码。“自动下载后手动安装”在这两个商店平台表示自动验证更新信息、明确点击后打开应用市场。

## 信任与校验

发布清单采用 ECDSA P-256 / SHA-256 签名。信封只包含标准 Base64 编码的 `payload` 和 `signature`；客户端先用内置公钥验签，再解析 JSON。每个平台的条目同时固定稳定版 SemVer、构建号、HTTPS 地址、文件大小和 SHA-256。预发布版本、降级、重复平台、未知字段、HTTP 地址、错误签名和被修改的包都会被拒绝。

正式私钥只保存在当前用户的 `%LOCALAPPDATA%\TabLink\release-signing\stable-private.pem`，不进入源码、安装包、下载站或诊断文件。公钥在 `updates/stable-public-key.spki.base64`，可公开随客户端分发。丢失私钥后，现有客户端无法信任由新密钥直接签出的清单，因此应由用户另行制作安全离线备份。

发布百分比使用本机随机安装标识稳定分桶，不读取手机序列号。清单从正式频道撤回某个平台的包后，该平台会丢弃尚未安装的旧待办记录。

## 各平台行为

| 平台 | 检查与下载 | 安装行为 |
| --- | --- | --- |
| Windows x64 | 按用户策略检查、下载并二次验签；活跃副屏期间延期 | “自动更新”在最后一个副屏会话停止并空闲后安装；“自动下载后手动安装”只响应明确安装操作；启动健康检查失败会恢复上一目录并重启旧版 |
| Android 直接分发 | 按用户策略在回到前台或定时检查，空闲时下载；投屏期间延期 | 使用 `PackageInstaller`；“自动更新”可自动发起安装，“自动下载后手动安装”只响应明确安装操作，两者都必须经过 Android 的系统安装确认 |
| 浏览器 | 页面资源内置在 Windows 主机中，响应禁用缓存 | 下次连接或受控重连时使用更新后的主机资源 |
| iPhone / iPad | 按三种策略读取并验证同一双源签名清单；“从不更新”不会发起请求 | 只在用户明确操作并再次确认最新签名决定后打开 App Store 地址；下载、审核和自动安装由 App Store 管理 |
| HarmonyOS NEXT | 按三种策略读取并验证同一双源签名清单；“从不更新”不会发起请求 | 只在用户明确操作后打开 AppGallery 地址；下载和自动安装由应用市场管理 |

Windows 下载包和签名清单保存在 `%LOCALAPPDATA%\TabLink\updates`。自动与手动受保护替换都只允许从精确的 `%ProgramFiles%\TabLink` 正式安装目录执行；从 E 盘、Desktop、OneDrive 或其他便携目录运行时仍可按策略检查并下载新版，但会提示从正式目录启动，且不会复制或启动提权更新器。真正执行替换的更新器四件套按内容哈希复制到 `%ProgramData%\TabLink\Updater\sha256-<包哈希>\`，解压、旧版备份和失败版本位于 `%ProgramData%\TabLink\Transactions\<事务号>\`。`Program Files`、`ProgramData`、正式安装目录和完整祖先链必须位于同一本地卷且不得包含重解析点；祖先目录允许 Windows 默认的创建权限，但拒绝普通用户删除或替换现有受保护子目录、改变 DACL 或取得所有权。正式安装树、TabLink 的 ProgramData 树和 helper 文件采用更严格的逐项策略，普通用户不能写入、删除或改变权限。受保护目录与文件由 Administrators 拥有、关闭继承，并只允许 SYSTEM 与 Administrators 写入；复制和启动前都会锁定并复核大小、SHA-256、owner 与 DACL。更新器先锁定并验签 ZIP、完成安全解压、逐文件复核 staging、验证旧安装全树 ACL，并保存旧树逐文件哈希；这些预检全部通过后才发送带 256 位随机挑战的就绪握手并让主程序退出。目录交换前会再做一轮包、staging、ACL 和旧树快照检查。新版必须在受保护事务目录中写入绑定 PID、启动时间、程序路径与随机挑战的健康信号，并继续运行至少 3 秒；失败时只有通过原快照复核的旧目录才能恢复和启动。

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

发布顺序必须是：

1. 把 `release` 目录内的版本包复制到下载站的 `Files\TabLink\stable\<version>`。
2. 从公网重新下载各版本包，核对大小和 SHA-256。
3. 最后把已验签的 `manifest.json` 复制为 `Files\TabLink\stable\manifest.json`。
4. 创建一个**非 prerelease** 的 GitHub 正式 Release，把所需的不可变版本包和同一信任密钥签署的 `manifest.json` 作为 Release assets；不要让 Preview Release 占用这条正式路径。若 GitHub 也承担包下载镜像，可另签一份只改变制品 URL 的 GitHub 清单，但 `releaseId`、`publishedAtUtc`、发布比例、协议门槛及每个平台的版本、构建号、大小、SHA-256、安装器 URL 和说明必须与博客清单一致。
5. 分别从博客主地址和 GitHub `releases/latest/download/manifest.json` 读取清单，用固定公钥验签，并再次核对清单所指包的大小和 SHA-256。任一源尚未就绪时，不得把“两个更新来源均可用”记为发布完成。

同一个版本号的包不可原地替换。需要回退时，应把旧代码重新构建成更高的新版本号并发布；Windows 目录级启动失败由本机更新器自动回滚。

## 尚需各平台账号完成的工作

iOS / iPadOS 工程还没有 Apple Developer Team、App Store Connect 记录、签名归档或 App Store ID。HarmonyOS NEXT 工程还没有 DevEco SDK 编译、正式签名、HAP、AppGallery 记录或真机验收。在这些条件完成前，正式清单不会伪造 `ios` 或 `harmony` 下载条目；源码中的更新适配器只会安静地报告该平台尚未发布。
