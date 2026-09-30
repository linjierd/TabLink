# TabLink 0.8.0 验证记录

验证日期：2026-09-29（Asia/Shanghai）

本文件记录 0.8.0 稳定频道、安装包和真实设备的验证证据。只把实际执行过的检查列为通过；Apple / HarmonyOS NEXT 没有本机工具链、商店签名和对应实机，因此它们的源码检查不等同于 IPA / HAP 交付。

## 更新信任链

- 稳定清单使用 ECDSA P-256 / SHA-256 签名；正式私钥位于源码树外的 `%LOCALAPPDATA%\TabLink\release-signing\stable-private.pem`。
- 私钥 ACL 已关闭继承，只允许当前账户读取/写入；源码、构建输出和发布暂存目录未发现 PEM 私钥内容。
- 固定公钥文件 `updates/stable-public-key.spki.base64` 的 SHA-256 为 `B275488873764E84A4FA3252D7EFAAFCBD13B97DB95A4D43C3F852748C12FECA`。
- Windows、Android 与发布工具使用同一份签名信封和互操作样本；payload 或签名被修改时都会拒绝。
- Windows 解压拒绝绝对路径、路径穿越、空路径段、重复路径、链接、重解析点和过度展开，并在同一个锁定 ZIP 流上完成哈希与解压。
- Android 在安装前重新验签保存的清单，重新计算 APK 大小和 SHA-256，并核对包名、versionName、versionCode 和当前安装签名。

## 自动更新测试

- Windows 更新测试：17 个场景、119 项断言通过。覆盖签名清单、正式 DownloadSite 查询地址、0% 暂停、降级拒绝、暂存包校验、安全解压、`%ProgramFiles%\TabLink` 正式目录限制、兼容标准 Windows ACL 的完整祖先命名空间检查、正式安装全树与 helper 文件严格 ACL、受保护的同卷 ProgramData 事务、完整预检后的随机握手、绑定进程身份的启动健康信号、旧树逐文件哈希快照及失败回滚。
- Android 更新安全及状态机：44 项断言通过；包含正式 DownloadSite 查询地址、URL 凭据与片段拒绝、整秒 UTC 规则及跨平台 cohort 固定向量。
- Android 既有回归：协议 55 项、视频队列 1,957 项、渲染时钟 26,024 项、HUD / 暂停策略 20 项、配对 / 二维码 / TLS 108 项通过。
- Android `assembleDebug`、`lintDebug` 和 `apksigner verify` 通过。
- ReleaseTool / publisher 的 18 项检查全部通过，覆盖构建、签名验签、跨端互操作、篡改拒绝、缺失私钥拒绝、HTTPS URL、重复版本拒绝、Windows 完整包预检，以及 Android 包名、版本、构建号与证书检查；错误包名 `com.example.not_tablink` 的独立 fixture 被明确拒绝。
- HarmonyOS NEXT：stable 更新策略 43 项、工程与关键安全路径 42 项、既有协议 84 项纯 Node / TypeScript 检查通过；这不是 DevEco / Harmony SDK 构建或 HAP 验证。
- Apple：Xcode 工程生成得到 16 个应用源文件和 14 个系统框架，Windows 可执行的源码 / 工程 / fixture 检查 151 项通过；`project.pbxproj` SHA-256 为 `505F216AF8534FBB4F208E812749DB5CCA78F1E3462363706B0DB24362C3E16D`。本机没有执行 Swift、XCTest、Xcode、签名或 Apple 真机验证。

## Android 0.8.0 候选

- 文件：`android/artifacts/TabLink-android-0.8.0-debug.apk`
- 包名：`com.tablink.client`
- `versionName=0.8.0`
- `versionCode=11`
- 大小：394,442 字节
- SHA-256：`DD7B26A4E3EB2AE8C69574528C0B5A822BBEFAC6C550C884E358FC8DA9F4B12D`
- 签名证书 SHA-256：`B0035FFE0539E43DED2F5C40E3B7E4D4EDFB5D8F8063459FACA911EDC7500554`
- 为保留当前 W202DS 的应用数据和原地升级能力，本版直装 APK 延续现有签名身份。签名文件已关闭继承，仅允许当前账户、SYSTEM 与 Administrators，并在 `%LOCALAPPDATA%\TabLink\android-signing\legacy-debug.keystore` 留有同哈希的受限 ACL 备份。
- 该签名身份用于现有直装频道，不代表 Google Play、App Store 或 AppGallery 的商店签名。

## 正式构建、发布与真实设备

本节在最终候选构建、公网下载复核和 W202DS 原地升级完成后填写。正式清单在两个不可变版本包完成公网大小和 SHA-256 复核前不会上线。

## 平台边界

- Windows 目标是空闲时自动安装；任何主连接、多设备连接或浏览器副屏仍活动时延期。
- Android 会自动检查和下载；普通应用能否无确认安装由 Android 系统决定。系统返回需要用户操作时，客户端打开标准安装确认页。
- 浏览器客户端内置于 Windows 主机并禁用缓存，随主机版本更新。
- iOS / iPadOS 只能把签名清单中的 App Store 地址交给系统；HarmonyOS NEXT 只能把签名清单中的 AppGallery 地址交给系统。没有商店记录时清单不会伪造这两个平台的制品。
