# TabLink 0.8.8 Preview 1 验证记录

验证日期：2026-10-01（Asia/Shanghai，离线候选已通过；实机与公共发布进行中）

本文只记录 0.8.8 的持久电脑身份、Android Keystore 设备身份、一次性登记、新挑战认证、局域网地址发现、跨 IP / Wi-Fi 与 USB 网络共享重连、撤销和认证前显示隔离。0.8.7 的受保护 USB 清理队列及更早版本的 90 fps 实机测量只作为回归背景，不能代替本版验证。

## 必须保持的不变量

- 二维码 bearer 自生成起只在五分钟内允许首次登记一次；单调时钟达到五分钟时必须拒绝。长期重连不保存或发送该 token。升级启动时删除旧 `pairing.lastLink` bearer。
- 已有可信设备时，后台自动启动可信监听不得发布二维码。只有用户显式点击“生成新配对二维码”才能生成 bearer；轮换后旧 token 必须立即拒绝，新 token 也只能成功使用一次。
- Windows 使用持久 TLS 证书确定 `hostId`；Android 私钥必须留在 Android Keystore，电脑只保存 P-256 公钥。
- 每次可信重连都使用新的 32 字节挑战。签名 transcript 同时绑定协议域、`hostId`、`deviceId` 与挑战；旧签名、错误设备、错误电脑或已撤销设备必须失败。
- UDP 27193 发现只返回地址提示。Android 必须在发现期限内收集有界的多个候选，不得信任首个响应；每个候选仍须通过精确证书钉扎和同一套接字上的签名挑战。发现不得携带 token、证书私钥、设备公钥列表或私密连接 URI。
- 只有认证完成并解析有效屏幕参数后才允许进入显示准备；认证失败、登记 token 重用、重放、撤销或发现探测不能安装或创建 VDD。
- 全局仍只有一个显示租约和一块 TabLink 虚拟副屏。登记设备数量、地址迁移或重连次数不能增加显示器上限。

## 已完成的最终离线门禁

以下检查已针对当前 0.8.8 工作树实际完成。唯一最终整仓库本地构建输出位于 `artifacts/v0.8.8-local-final-20261001-232713/`；它使用回环网络、E 盘测试目录、合成协议数据和 fake 显示准备，没有访问真实手机、平板、ADB、Windows 驱动或公共发布服务：

| 检查 | 已取得的结果 | 证据边界 |
| --- | --- | --- |
| Windows Release 编译，浏览器功能启用 | 0 个警告、0 个错误 | `EnableBrowserReceiver=true`，当前最终源码。 |
| Windows Release 编译，浏览器功能禁用 | 0 个警告、0 个错误 | `EnableBrowserReceiver=false`，覆盖公共包组合。 |
| `TabLink.TrustedPairing.Tests` | 81 条断言通过 | 包含双端 transcript 固定向量、严格 discovery 解析、每 IP 合法请求限速、256 地址上限及 SocketException 可取消恢复。 |
| `TabLink.Transport.Tests` | 完整套件通过 | 覆盖一次登记、持久 host ID、新挑战签名重连、五分钟边界拒绝、显式轮换后旧 token 拒绝、新 token 仅一次、旧签名重放拒绝和撤销拒绝；全部断言显示准备之前失败关闭。 |
| 其余 Windows 回归 | 全部通过 | Core 46；ADB locator 22；Browser 37；驱动配置 89；分配快照 30；单屏清理 22 场景 / 96 断言；稳定显示身份 40；显示生命周期 192；连接健康 13 场景 / 45；诊断 14 场景 / 94；USB lease 48；USB recovery 135；更新 17 场景 / 119；编码器、传输自检与其他套件全部通过。 |
| Android 最终 debug 构建 | JVM 全套、`assembleDebug`、`lintDebug`、APK v1/v2 签名全部通过 | `artifacts/v0.8.8-local-final-20261001-232713/android/TabLink.apk`，553,114 字节，SHA-256 `3304DF2D4B815B4AAE8FE034AF9D1EE5D7D51111F9EA55558C885E9091EE4706`。新增首次/替换外部深链确认、活动连接抗外部 Intent 中断、忘记可信电脑失败回滚与不确定状态清理、多候选发现测试均通过。 |
| Android Release Preview 构建 | `assembleRelease`、`lintRelease`、v1/v2 签名、签名身份和包身份门禁全部通过 | 最新源码随后单独执行 `android/build.ps1 -ReleasePreview`；本地 APK 为 344,031 字节，SHA-256 `2871A577572BCBAB9B0E833743216DF7B1BBAF3AC27BB5B3427DEC759B5DFAAA`。最终公开 APK 的字节数与 SHA-256 仍只以干净提交的公共构建和 GitHub 下载副本为准。 |
| 跨平台 transcript | 双端固定 SHA-256 `1F57A15130EE260C4242840D79E543CFA0843976E42B0989C628D250E305AFB9` 通过 | 只验证规范字节，不代替 Android Keystore 实机签名。 |
| 发布入口门禁 | `-PublicRelease -SkipAndroid` 按预期立即失败 | 实际错误明确要求公共包从同一干净提交重建、lint、识别并验证 Android APK。 |

离线测试还覆盖了认证 JSON 的未知字段、重复字段和长度边界，设备 ID 与 P-256 SPKI 的 SHA-256 绑定，以及发现请求的严格字段、nonce 和同子网判断。生产信任目录使用管理员保护的 ProgramData 边界，PFX 另外使用 DPAPI CurrentUser；测试通过注入存储边界留在 E 盘，不创建或修改真实 `%ProgramData%\TabLink\NativeTrust\`。

## 最终离线门禁结果

当前工作树的本地离线门禁已经完成：

- [x] Windows browser-enabled 与 browser-disabled Release 编译均为 0 warning / 0 error。
- [x] `TabLink.TrustedPairing.Tests` 81 条断言及双端 transcript 固定向量通过。
- [x] `TabLink.Transport.Tests` 完整通过五分钟边界、轮换、单次使用、登记、挑战、重放、撤销与认证前零显示准备。
- [x] Core、显示分配与清理、USB lease/recovery、浏览器、更新器、编码器、诊断和生命周期回归全部通过。
- [x] Android JVM 最终套件、`assembleDebug` / `lintDebug`、Release Preview `assembleRelease` / `lintRelease` 全部通过。
- [x] `apksigner --print-certs` 得到既有签名证书 SHA-256 `b0035ffe0539e43ded2f5c40e3b7e4d4edfb5d8f8063459faca911edc7500554`；构建脚本对密钥缺失或签名不符失败关闭。
- [x] `aapt` 已核对包名 `com.tablink.client`、`versionName 0.8.8`、`versionCode 20`；本地 Release Preview APK 的 v1/v2 签名与 SHA-256 已核对。
- [x] `git diff --check` 无空白错误；当前只见仓库既有 LF/CRLF 转换提示。
- [ ] 提交后仍须从干净发布提交执行 `-PublicRelease` 并生成公共资产清单；未提交候选的 ProductVersion 仍引用上一个提交，因此不得直接发布。

## W202DS 实机验证：待验证

本节尚未执行 0.8.8 实机操作。后续只允许对已授权的 W202DS 执行验证；不得向排除列表中的 F50 Pro 或其他设备发送安装、ADB、USB 切换或网络命令，也不得在文档、日志或发布资产中记录真实设备序列号。

- [ ] 覆盖安装 0.8.8 APK，并由包管理器确认包名、versionName 和 versionCode。
- [ ] 已有可信设备时启动 Windows 应用，确认可信监听自动运行但界面不发布二维码；显式点击“生成新配对二维码”后才出现五分钟配对码。
- [ ] 清除旧可信记录后扫描一次二维码；确认电脑登记一条设备公钥，二维码 token 再次使用被拒绝。另生成两枚配对码，确认第二枚生成后第一枚立即拒绝，第二枚也只能成功使用一次。
- [ ] 断开并重启 Android 客户端，无需扫码即可通过固定证书和新挑战恢复；连续两次挑战应不同。
- [ ] 重启 Windows TabLink 后保持相同 `hostId`，平板无需扫码恢复；不得因为程序重启生成新电脑证书。
- [ ] 在 Wi-Fi 与 USB 网络共享之间切换或使电脑 IPv4 改变；若原所选线路仍保持 Up，先在电脑端选择目标线路并重建监听，再确认先前信任从有界的多个 UDP 地址候选中找到新端点。每个实际尝试的候选仍须先完成精确 TLS pin，再在同一套接字完成签名挑战。
- [ ] 连接时撤销当前设备；确认会话立即停止、输入释放、唯一副屏按既有生命周期收回，之后自动重连被拒绝。
- [ ] 发送错误签名、旧挑战或未登记设备时，确认 Windows 中没有新增活动 TabLink VDD；成功认证后也始终最多一块。
- [ ] 确认原生 1200 × 1920 / 90 Hz、实际 decoder、解码提交与呈现回调；需要引用 SurfaceFlinger 时另行保存原始时间戳，不能把请求 90 Hz 当成最终呈现 90 fps。
- [ ] 正常停止后确认 TabLink 会话、TCP/UDP 防火墙规则和活动 VDD 清理结果；持久电脑证书与已登记公钥应保留，供下次重连。

## 公共构建与发布：待验证

当前没有 0.8.8 公共构建或 GitHub Release 可以据此声明成功。发布前必须完成：

- [ ] 确认 `-PublicRelease -SkipAndroid` 在构建入口立即失败；随后从干净发布提交执行不跳过 Android 的 `-PublicRelease`，记录提交 SHA、Windows FileVersion/ProductVersion、Android 身份和构建日志。
- [ ] 在公共构建日志与最终 APK 上再次确认固定签名 SHA-256、包名 `com.tablink.client`、`versionCode 20`、`versionName 0.8.8`；任何缺失或不匹配都必须阻止发布。
- [ ] 核对 Windows self-contained x64 ZIP、Android preview APK、源码/许可包和 `SHA256SUMS.txt` 的文件名、字节数与 SHA-256。
- [ ] 从 GitHub Release 实际下载全部公开资产，并对下载副本重新计算哈希、检查 ZIP 内容、Windows 版本和 APK 签名/身份。
- [ ] 确认 tag、GitHub Actions、Release 页面和交付目录均指向同一发布提交，不混用本地未提交候选。
- [ ] 再次从正式 HTTPS 地址下载并验签稳定清单，确认 releaseId 和平台版本仍为 **0.8.0**；0.8.8 Preview 1 不得推进 `stable`。

公共 ZIP 的最终外层哈希不能写回该 ZIP 内的本文，否则会形成自引用并改变资产。最终资产级文件名、大小、SHA-256 和 GitHub 下载复核以 Release 外层 `SHA256SUMS.txt` 与发布记录为准；发布后在 `main` 补写的 post-release 证据不改变发布 tag，也不表示 tag 内的本文包含自身 ZIP 的最终哈希。

## 当前结论

0.8.8 的最终本地离线源码门禁已经通过，包括一次登记、新挑战认证、发现消息最小化、二维码五分钟边界与轮换、撤销早于显示准备、单副屏约束、Android debug/release 构建和预览签名身份。Android Keystore 实机、W202DS 连接与撤销清理、干净提交公共构建及 GitHub 下载后复核仍待完成，因此当前仍是已通过离线门禁的发布候选，尚不能称为公共发布或实机验收完成。
