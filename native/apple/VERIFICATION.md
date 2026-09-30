# Apple 原生源工程验证记录

日期：2026-09-29；执行主机：Windows。范围：`native/apple/`。

| 项目 | 状态与证据 |
| --- | --- |
| 工程生成 | `python tools/generate_project.py` 成功；16 个 Swift / Metal 应用源文件，14 个系统框架引用 |
| 本地静态校验 | `python tools/verify_source.py`：151 项通过；检查 artifact / fixtures / 更新公钥、正式地址、跨端 signed-envelope 与源代码关键路径存在，不运行 Swift |
| Xcode 工程 | 已生成 `TabLink.xcodeproj/project.pbxproj` 与 shared scheme；PBX 引用完整、plist 可解析、scheme XML 可解析 |
| 纯协议测试源 | 8 个 XCTest 方法已提供，包括固定 P-256 清单验签、篡改拒绝、strict stable/cohort；**未执行** |
| Swift 编译 / Xcode 构建 | **未执行**：当前可用 Windows 环境没有 `swift` 或 `xcodebuild` 命令，没有 Apple SDK |
| 签名 / IPA | **没有**：未访问签名凭据，没有 signing Team 或 provisioning profile |
| 真机联网 / 硬解 / 实际呈现 / App Store 更新 | **未验证**：不能从源码或 Python 检查推导已成功 |
| 设备操作 | 未安装客户端、未操作 iPad / iPhone / Android，未停止当前 Windows 会话 |

`project.pbxproj` SHA-256：

```text
505f216af8534fbb4f208e812749db5cca78f1e3462363706b0db24362c3e16d
```

已人工核对 Windows FrameServer 的包头、认证 / profile 顺序、0x20 / 0x21 帧格式和 0x12 ACK 契约。已查阅 Apple 官方 API，确认硬件解码强制 / 查询键要求 iOS / iPadOS 17。项目不实现 0x14 较弱的 render-submitted 证据，0x12 仅由 Metal 的实际 presented 回调触发。

更新适配只在签名清单验证通过后显示 `apps.apple.com` 入口；不下载或替换 IPA。当前 Windows 检查未执行 CryptoKit、`UIApplication.open`、App Store 安装或系统自动更新。下一步必须在 Mac 执行 README 的 Swift / Xcode 命令，再完成真机验证清单。
