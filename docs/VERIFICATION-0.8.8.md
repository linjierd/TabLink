# TabLink 0.8.8 Preview 1 验证记录

验证日期：2026-10-02（Asia/Shanghai，离线门禁、W202DS 核心路径、公共发布与公开回下载复核已通过；一次性 token/二维码轮换/新挑战、物理线路迁移、活动撤销和整机重启仍待实机验证）

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

以下检查已针对当前 0.8.8 工作树实际完成。修复退休 generation 扫描后的完整 Windows 本地构建输出位于 `artifacts/v0.8.8-postfix-local-20261002-001709/`；Android 源码没有随该修复变化，其最后一次完整 debug 构建仍位于 `artifacts/v0.8.8-local-final-20261001-232713/`。这些门禁使用回环网络、E 盘测试目录、合成协议数据和 fake 显示准备，没有访问真实手机、平板、ADB、Windows 驱动或公共发布服务：

| 检查 | 已取得的结果 | 证据边界 |
| --- | --- | --- |
| Windows Release 编译，浏览器功能启用 | 0 个警告、0 个错误 | `EnableBrowserReceiver=true`，修复后的当前源码；输出 `artifacts/v0.8.8-postfix-local-20261002-001709/`。 |
| Windows Release 编译，浏览器功能禁用 | 0 个警告、0 个错误 | `EnableBrowserReceiver=false`，覆盖公共包组合。 |
| `TabLink.TrustedPairing.Tests` | 81 条断言通过 | 包含双端 transcript 固定向量、严格 discovery 解析、每 IP 合法请求限速、256 地址上限及 SocketException 可取消恢复。 |
| `TabLink.Transport.Tests` | 完整套件通过 | 覆盖一次登记、持久 host ID、新挑战签名重连、五分钟边界拒绝、显式轮换后旧 token 拒绝、新 token 仅一次、旧签名重放拒绝和撤销拒绝；全部断言显示准备之前失败关闭。 |
| 其余 Windows 回归 | 全部通过 | Core 46；ADB locator 22；Browser 37；驱动配置 93；分配快照 30；单屏清理 22 场景 / 96 断言；稳定显示身份 40；显示生命周期 192；连接健康 13 场景 / 45；诊断 14 场景 / 94；USB lease 48；USB recovery 135；更新 17 场景 / 119；编码器、传输自检与其他套件全部通过。最终公共构建已从干净提交 `7c20a72c5d77cd454a4115d4a763d668f002f329` 重跑同一套门禁并通过。 |
| Android 最终 debug 构建 | JVM 全套、`assembleDebug`、`lintDebug`、APK v1/v2 签名全部通过 | `artifacts/v0.8.8-local-final-20261001-232713/android/TabLink.apk`，553,114 字节，SHA-256 `3304DF2D4B815B4AAE8FE034AF9D1EE5D7D51111F9EA55558C885E9091EE4706`。新增首次/替换外部深链确认、活动连接抗外部 Intent 中断、忘记可信电脑失败回滚与不确定状态清理、多候选发现测试均通过。 |
| Android Release Preview 构建 | `assembleRelease`、`lintRelease`、v1/v2 签名、签名身份和包身份门禁全部通过 | 干净发布提交生成的最终公共 APK 为 344,075 字节，SHA-256 `C2CEA0B404B0B624E77AE9CB67B6F7F9B19CB4483A1FD39E853823361945AFD7`；本表只记录构建侧证据，GitHub 回下载结果另见“公共构建与发布”一节。 |
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
- [x] 已从干净发布提交 `7c20a72c5d77cd454a4115d4a763d668f002f329` 执行完整 `-PublicRelease`；`TabLink.exe` 的 FileVersion 为 `0.8.8.0`，ProductVersion 为 `0.8.8+7c20a72c5d77cd454a4115d4a763d668f002f329`，公共资产清单已生成并复核。

## W202DS 实机验证：核心路径已通过

本节只记录已授权 W202DS 的 0.8.8 实机结果。没有向排除列表中的 F50 Pro 或其他设备发送安装、ADB、USB 切换或网络命令；文档、日志和发布资产不记录真实设备序列号。

- [x] 本轮公开环境为 Windows 11 x64、NVIDIA RTX 4060 Laptop GPU 与 Android 13 的中兴 W202DS。W202DS 原生竖屏为 `1200 × 1920`，报告支持 60 / 90 Hz；本轮横屏 `1920 × 1200` 对应原生方向旋转 1/4 圈。这里只记录公开商品型号和非唯一能力信息。
- [x] 覆盖安装公共候选 APK；从设备回读的安装包与候选逐字节一致：包名 `com.tablink.client`、`versionName 0.8.8`、`versionCode 20`、344,075 字节、SHA-256 `C2CEA0B404B0B624E77AE9CB67B6F7F9B19CB4483A1FD39E853823361945AFD7`。
- [x] 使用五分钟登记链接首次登记；Android 外部链接先显示“确认连接这台电脑”，用户确认后 Windows 只新增一条可信设备记录。没有在文档或日志中保存二维码 bearer。
- [x] 停止并重新启动 Android 客户端时没有再次传入配对 URI；客户端通过已保存的电脑证书固定和签名挑战自动恢复连接。
- [x] Windows TabLink 重新启动后仍使用已有信任，平板无需重新扫码恢复；没有观察到第二条可信设备记录或第二块 VDD。
- [x] 成功连接时 Windows 只有一块 TabLink VDD 和两块活动桌面；W202DS 当前方向上报并使用 `1920 × 1200`、屏幕 90 Hz / 请求 90 Hz。Android 解码器为 `c2.unisoc.avc.decoder`，电脑端编码为 NVENC / 90 fps；实测解码提交约 90.1、呈现回调约 90.0 帧/秒。这两个客户端计数不等同于 SurfaceFlinger 或外部相机测得的物理呈现帧率，本轮 0.8.8 核心路径没有记录物理呈现测量值。
- [x] 找到并修复正常停止后的精确卸载阻塞：最终 generation 已退休时，同一进程 incarnation 的临时 bootstrap 已失去 marker 权限，但旧扫描仍把它误判为活动 owner。修复仅在空 marker 分支接受相同 PID 加相同进程启动时间；非空 marker、缺 marker、不同 owner、PID 复用和损坏状态继续失败关闭。定向驱动配置测试现为 93 条断言，并保留 192 条显示生命周期、22 场景 / 96 条单屏清理和 30 条分配回归。
- [x] 在同一 Windows 进程中连续完成两轮“连接 → 正常停止”：每轮连接后活动屏为 2、PresentOnly VDD 为 1；停止后活动屏为 1、PresentOnly VDD 为 0，页面显示“已停止连接，虚拟副屏设备已卸载”。第二轮连接没有被旧 pending cleanup 拦截。
- [ ] 尚未实机重放同一登记 token、轮换两枚二维码或逐次记录两个挑战值；对应协议边界已有离线固定向量和状态机测试，不把它们写成实机通过。
- [ ] 尚未在 Wi-Fi 与 USB 网络共享之间执行线路迁移或 IPv4 改址验证。
- [ ] 尚未在活动连接中撤销当前设备并验证后续自动重连拒绝。
- [ ] 尚未执行整台电脑重启后的可信重连；本轮只验证了 Windows 应用进程重启和 Android 应用进程重启。

## 公共构建与发布：已完成

- [x] `-PublicRelease -SkipAndroid` 在构建入口按预期立即失败；随后从干净发布提交 `7c20a72c5d77cd454a4115d4a763d668f002f329` 执行不跳过 Android 的完整 `-PublicRelease`。Windows FileVersion 为 `0.8.8.0`，ProductVersion 为 `0.8.8+7c20a72c5d77cd454a4115d4a763d668f002f329`。
- [x] 最终 APK 的固定签名证书 SHA-256 为 `b0035ffe0539e43ded2f5c40e3b7e4d4edfb5d8f8063459faca911edc7500554`，包名为 `com.tablink.client`、`versionCode 20`、`versionName 0.8.8`，v1/v2 签名通过。
- [x] GitHub [TabLink 0.8.8 Preview 1](https://github.com/linjierd/TabLink/releases/tag/v0.8.8-preview.1) 已作为预发行版发布；annotated tag `v0.8.8-preview.1` 精确指向同一发布提交。
- [x] 发布提交对应的 [GitHub Actions run 36891751228](https://github.com/linjierd/TabLink/actions/runs/36891751228) 为 `completed / success`，`windows-managed-tests`、`Build Windows projects` 与 `Run managed tests` 均为 `success`。该 workflow 只覆盖 Windows managed CI，不代替 Android、公共打包和下载复核。

| 公开资产 | 字节数 | SHA-256 |
| --- | ---: | --- |
| `TabLink-Windows-x64-0.8.8-preview.1.zip` | 97,277,498 | `BC33511C2481AB09CC7F4AF725E7AA29F3C7318C0C65FCD9113AB614240C22C2` |
| `TabLink-Android-0.8.8-preview.1.apk` | 344,075 | `C2CEA0B404B0B624E77AE9CB67B6F7F9B19CB4483A1FD39E853823361945AFD7` |
| `TabLink-FFmpeg-7.0.2-corresponding-source.tar.gz` | 28,919,316 | `FD7977F53EDD262D55C49F200EB5F54B1B12F5FFA547770380448708D75EA6F2` |
| `SHA256SUMS.txt` | 326 | `960EBAC583853F35C1B11BDA325C86A771C42F02F505F3F77CB6F481BAD185E1` |

- [x] 四项资产已从 GitHub Release 公开 URL 下载到独立 E 盘目录并重新计算哈希，结果与发布前资产及 GitHub 页面 digest 一致。Windows ZIP 有 462 个文件；与最终公共构建逐文件比对后，重复、越界、缺失、额外和内容哈希差异均为 0。公开 APK 与实机已安装候选具有同一 SHA-256，因此沿用已通过的包身份和签名结论。
- [x] 源码树交付目录、桌面输出副本和 OneDrive 副本均已同步为最终公共构建；每个目录包含 462 个文件，全部预期文件逐项哈希通过，`TabLink.exe` SHA-256 均为 `4310D8D78084A14AD6A6C5089F784AEE10AEBCD14F4C7346289BECA16E6021B7`。公开记录不保留贡献者电脑上的绝对路径。
- [x] 已从正式 HTTPS 地址重新下载稳定清单并使用固定 P-256 公钥按 ECDSA/SHA-256 验签。清单 `releaseId` 为 `tablink-0.8.0`，Windows 与 Android 平台版本均为 `0.8.0`，rollout 为 100%；0.8.8 Preview 1 没有推进 `stable`。

公共 ZIP 的最终外层哈希不能写回该 ZIP 内的本文，否则会形成自引用并改变资产。最终资产级文件名、大小、SHA-256 和 GitHub 下载复核以 Release 外层 `SHA256SUMS.txt` 与发布记录为准；发布后在 `main` 补写的 post-release 证据不改变发布 tag，也不表示 tag 内的本文包含自身 ZIP 的最终哈希。

## 当前结论

0.8.8 的本地离线源码门禁、W202DS 核心实机路径、干净发布提交公共构建、GitHub 预发行和公开回下载复核已经通过，包括一次登记、Android Keystore 签名重连、Windows 应用重启后的信任延续、单副屏约束、1920 × 1200 / 90 Hz 呈现、连续两轮正常停止后的精确 VDD 卸载，以及 462 个 Windows 包内文件的逐项一致性。当前可以称为 **0.8.8 Preview 1 公共预发行完成**。同一登记 token 的实机重放拒绝、两枚二维码轮换、逐次挑战值、Wi-Fi/USB 网络共享线路迁移、活动连接中的撤销即时生效和整台电脑重启后的可信重连仍是明确未通过的实机边界。
