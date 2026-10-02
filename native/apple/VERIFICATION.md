# Apple 原生源工程验证记录

日期：2026-10-02；执行主机：Windows。范围：`native/apple/`。

| 项目 | 状态与证据 |
| --- | --- |
| 工程生成 | `python tools/generate_project.py` 成功；17 个 Swift / Metal 应用源文件，14 个系统框架引用 |
| 本地静态校验 | `python tools/verify_source.py`：191 项通过；检查 artifact / fixtures / 更新公钥、博客与 GitHub 正式地址、三种更新模式、三种语言选择、en/zh-Hans 资源键与格式占位符完全一致、规范镜像语义、来源选择、持久冲突阻断、防回退、跳转前重验、跨端 signed-envelope 与源代码关键路径存在，不运行 Swift |
| Xcode 工程 | 已生成 `TabLink.xcodeproj/project.pbxproj` 与 shared scheme；PBX 引用完整、英文/简中 variant group 已进入 Resources phase、plist 可解析、scheme XML 可解析 |
| 纯协议测试源 | 9 个 XCTest 方法已提供，包括固定 P-256 清单验签、篡改拒绝、strict stable/cohort、模式损坏失败关闭、URL-only / 顺序不同镜像等价、installer / rollout 冲突、暂停权威和防回退；**未执行** |
| Swift 编译 / Xcode 构建 | **未执行**：当前可用 Windows 环境没有 `swift` 或 `xcodebuild` 命令，没有 Apple SDK |
| 签名 / IPA | **没有**：未访问签名凭据，没有 signing Team 或 provisioning profile |
| 真机联网 / 硬解 / 实际呈现 / App Store 更新 | **未验证**：不能从源码或 Python 检查推导已成功 |
| 设备操作 | 未安装客户端、未操作 iPad / iPhone / Android，未停止当前 Windows 会话 |

`project.pbxproj` SHA-256：

```text
19cc5d7c3407009963a588b054947de5f529e60f3a7b51623a66df3f7873671a
```

已人工核对 Windows FrameServer 的包头、认证 / profile 顺序、0x20 / 0x21 帧格式和 0x12 ACK 契约。已查阅 Apple 官方 API，确认硬件解码强制 / 查询键要求 iOS / iPadOS 17。项目不实现 0x14 较弱的 render-submitted 证据，0x12 仅由 Metal 的实际 presented 回调触发。

更新适配从博客与 GitHub 固定地址读取清单，只在固定公钥验签、规范发布语义比较、最新决定选择和防回退检查全部通过后显示 `apps.apple.com` 入口；“从不更新”在源码路径上取消并禁止网络检查。包 URL 可作为镜像变化，其他发布语义仍参与冲突检查。客户端不下载或替换 IPA，所有商店跳转都要求用户明确点击，并会在跳转前重新验证当前双源决定。当前 Windows 检查未执行 XCTest、CryptoKit、`URLSession` 取消、`UIApplication.open`、App Store 安装或系统自动更新。下一步必须在 Mac 执行 README 的 Swift / Xcode 命令，再完成三种模式的真机网络观察、双源冲突 / 回退 fixture 和 App Store 跳转验证。

语言选择保存在 `UserDefaults` 的 `appLanguageV1`；取值为 `system`、`zh-Hans` 或 `en`。缺失/损坏值回到 `system`。系统模式读取 `Locale.preferredLanguages.first`，所有 `zh-*` 显式使用 `zh-Hans`，其他系统语言使用 `en`。静态检查确认该分支、两种语言资源键完全相同、值非空、Swift 引用键存在且工程会复制资源；**没有在 Xcode、模拟器或 Apple 真机验证 Bundle 选择、运行时切换、InfoPlist 权限文字、动态字体或布局。**
