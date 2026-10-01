# TabLink 0.8.8 Preview 1：可信设备与跨线路重连

发布日期：2026-10-02

TabLink 0.8.8 Preview 1 把原生 Android 网络连接从“每次会话保存并重用 bearer 链接”改为长期设备身份。第一次扫描二维码只登记一次 Android 公钥；二维码 bearer 只有五分钟有效期且只能成功使用一次。以后每条 TCP 连接都必须固定电脑证书并完成新的签名挑战。电脑 IPv4 变化，或平板在 Wi-Fi 与 USB 网络共享之间切换时，可以寻找同一台已登记电脑并重新认证，无需为了地址变化重复扫码。

这次变化没有放宽显示器上限。TabLink 仍然全局只允许一台接收设备占用一块虚拟副屏，且认证和屏幕参数校验完成前不创建、安装或准备 VDD。

## 可信身份

- Windows 第一次使用原生网络连接时生成 3072 位 RSA 自签名 TLS 证书。证书、主机身份和已登记设备公钥保存在受保护的 `%ProgramData%\TabLink\NativeTrust\`；PFX 还使用当前 Windows 用户的 DPAPI 保护。后续监听继续使用同一证书，`hostId` 为证书 DER 的 SHA-256。
- Android 在系统 Android Keystore 中生成不可导出的 P-256 私钥。`deviceId` 是对应 SPKI 公钥的 SHA-256；电脑只保存公钥、脱敏设备身份、显示名称和使用时间，不接收私钥。
- 首次扫码仍使用 TLS 1.2/1.3 和证书固定。二维码中的 256 位随机 token 自生成起五分钟内只授权一次登记：客户端声明 `trusted-device-v1` 并提交设备公钥后，电脑在返回登记确认前消费 token。同一二维码不能再登记第二台设备，达到五分钟时也会被拒绝。
- 已有可信设备时，电脑会自动启动可信监听，但不会发布新的二维码。用户需要登记设备时显式点击“生成新配对二维码”；生成新码会立即作废旧 token，新 token 仍只有五分钟有效期且只能成功使用一次。
- 后续重连不发送 bearer token。电脑每次生成新的 32 字节随机挑战；Android 使用 `SHA256withECDSA` 签署绑定协议域、`hostId`、`deviceId` 和挑战的规范 transcript。旧签名无法用于另一次连接。
- Android 启动时删除旧版 `SharedPreferences` 中的 `pairing.lastLink` bearer 记录。新的 `trustedComputer` 只保存公开的电脑身份、证书指纹、最后 IPv4 和端口。

## 地址发现与网络迁移

- 已登记客户端先尝试上次成功的 IPv4/端口；连接失败后，向当前 IPv4 网络的广播地址发送 UDP 27193 发现请求。Android 在完整发现期限内收集有界的多个候选，最多八个且每个来源 IP 一个，而不是信任第一个响应。
- 请求只含协议版本、目标 `hostId` 和随机 nonce；响应只含相同 nonce、`hostId` 和当前 TCP 端口。电脑只响应所选接口同一子网内的请求，并对每个来源地址限速。
- 每个发现结果都只是一条不可信的路由提示。Android 逐个连接候选，并在同一套接字上精确核对登记时固定的持久 TLS 证书，再完成新的 P-256 签名挑战；发现服务不返回 token、设备公钥列表、证书私钥或带凭证的连接链接。
- 当前所选接口的 IPv4 变化或接口消失时，后台会刷新线路并恢复监听。若原线路仍保持 Up 而另一条 USB/Wi-Fi 线路新出现，电脑端需要先选择目标线路并重建监听；已有设备信任可以沿用，不必因为地址变化重新扫码。路由器客户端隔离、VPN/TUN、不同子网或禁用广播仍可能阻止发现。

## 撤销与显示生命周期

- Windows “可信设备”列表显示登记名称和脱敏 ID，并允许逐个撤销。撤销当前连接的设备会停止会话、释放输入并进入既有单副屏清理流程；以后来自该 `deviceId` 的 hello 或签名都会被拒绝。
- Android “忘记上次配对的电脑”同时删除本地公开信任元数据与相应 Keystore 密钥。再次连接必须扫描电脑生成的新二维码。
- 首次登记、可信重连、错误签名、重放签名、已撤销设备和局域网发现都在显示准备之前处理。只有认证完成并收到有效 `0x13` 屏幕参数后，电脑才调用显示准备。
- 全局显示租约上限保持为一。登记多台可信设备不会创建第二、第三或第四块虚拟显示器；当前已有接收端占用或准备副屏时，其他请求继续在驱动管理之前被拒绝。
- 正常停止时，驱动 helper 现在能识别同一 Windows 进程内已被最终 generation 取代的临时显示租约。该例外只适用于 marker 已明确退休、PID 与进程启动时间同时一致的旧 bootstrap；活动 marker、缺失 marker、PID 复用或其他 owner 仍会阻止卸载。W202DS 已连续两轮验证停止后活动桌面回到一块，精确 VDD 节点也随会话卸载。

## 保持不变

- 原生协议主版本仍为 v1。H.264、90 fps 修复、自适应画质、decoder recovery、健康中心、ADB USB 兼容通道及受保护 USB 清理队列继续沿用 0.8.7 的行为。
- Wi-Fi 与 USB 网络共享仍不需要 ADB、USB 调试或开发者模式；TabLink 不切换 USB 产品模式、不修改默认路由或 DNS。
- 已签名的公网 `stable` 自动更新频道继续保持 **0.8.0**。发布 0.8.8 预览包不会推进稳定清单，也不会让现有稳定版用户自动安装本预览。

## 兼容与已知边界

- 本次长期设备身份先覆盖原生 Android 网络客户端。浏览器接收、ADB loopback 兼容通道、Apple 原生工程和 HarmonyOS NEXT 原生工程没有因此获得 Android Keystore 挑战流程。
- 持久电脑证书由当前 Windows 用户的 DPAPI 保护。删除受保护信任目录、证书损坏或改用无法解密该 PFX 的 Windows 身份，会形成新的电脑身份，原 Android 信任需要重新登记。
- 设备名称只是界面标签；授权依据始终是已登记 P-256 公钥及其 `deviceId`。局域网中的发现响应也不构成授权。
- 自动发现只服务于同一 IPv4 子网。网络广播被阻止时，用户仍可在目标线路生成新二维码；这会创建新的登记流程，而不会绕过固定证书。
- W202DS 首次登记、Android/Windows 应用进程无扫码重连、90 Hz 呈现和连续两轮精确 VDD 卸载已经通过；干净提交公共构建、GitHub Release 和公开回下载复核也已完成。同一登记 token 的实机重放拒绝、二维码轮换、逐次挑战值、整台电脑重启、Wi-Fi/USB 网络共享迁移及活动连接中的撤销即时生效仍以 [验证记录](VERIFICATION-0.8.8.md) 为准。标为“待验证”的项目不是已经通过的发布声明。

## 安装

Windows 公共包为 `TabLink-Windows-x64-0.8.8-preview.1.zip`，是 self-contained x64 包。完整解压后运行 `TabLink.exe`；程序会请求管理员权限，用于按连接生命周期维护唯一虚拟显示设备和受保护信任状态。Android 预览包为 `TabLink-Android-0.8.8-preview.1.apk`，其 `versionName` 为 `0.8.8`、`versionCode` 为 `20`，可在签名连续时覆盖安装并删除旧 bearer 配对记录。

公共构建拒绝组合使用 `-PublicRelease -SkipAndroid`，不能发布只构建 Windows 而跳过 Android 身份门禁的包。Android `-ReleasePreview` 要求已有签名身份，其证书 SHA-256 必须精确为 `b0035ffe0539e43ded2f5c40e3b7e4d4edfb5d8f8063459faca911edc7500554`；密钥缺失或签名不匹配时构建失败，不会临时生成新的预览签名。构建脚本还必须核对包名 `com.tablink.client`、`versionCode 20` 和 `versionName 0.8.8`。这些门禁已在提交 `7c20a72c5d77cd454a4115d4a763d668f002f329` 的最终公共构建和 GitHub 回下载副本上通过，实际结果见 [验证记录](VERIFICATION-0.8.8.md)。

最终资产文件名、字节数、SHA-256 与下载复核记录放在 GitHub Release 外层 `SHA256SUMS.txt` 和发布记录中。包内 [VERIFICATION-0.8.8.md](VERIFICATION-0.8.8.md) 记录构建门禁、APK/Windows 身份和 W202DS 实机范围，但不自引用其所属 ZIP 的最终外层哈希；发布后在 `main` 补充的下载证据属于 post-release 记录，不改变已发布 tag 或资产。

## 发布结果（post-release）

- GitHub Release：[TabLink 0.8.8 Preview 1](https://github.com/linjierd/TabLink/releases/tag/v0.8.8-preview.1)，标记为 `prerelease=true`；annotated tag `v0.8.8-preview.1` 指向提交 `7c20a72c5d77cd454a4115d4a763d668f002f329`。
- 发布提交的 [GitHub Actions run 36891751228](https://github.com/linjierd/TabLink/actions/runs/36891751228) 已完成并成功；Windows 项目编译和 managed tests 均为 `success`。该 workflow 不覆盖 Android 构建、公共打包和 Release 回下载，后两项由下列独立复核补足。

| 公开资产 | 字节数 | SHA-256 |
| --- | ---: | --- |
| `TabLink-Windows-x64-0.8.8-preview.1.zip` | 97,277,498 | `BC33511C2481AB09CC7F4AF725E7AA29F3C7318C0C65FCD9113AB614240C22C2` |
| `TabLink-Android-0.8.8-preview.1.apk` | 344,075 | `C2CEA0B404B0B624E77AE9CB67B6F7F9B19CB4483A1FD39E853823361945AFD7` |
| `TabLink-FFmpeg-7.0.2-corresponding-source.tar.gz` | 28,919,316 | `FD7977F53EDD262D55C49F200EB5F54B1B12F5FFA547770380448708D75EA6F2` |
| `SHA256SUMS.txt` | 326 | `960EBAC583853F35C1B11BDA325C86A771C42F02F505F3F77CB6F481BAD185E1` |

四项资产已从公开 Release URL 重新下载到 E 盘独立验证目录并重新计算哈希。Windows ZIP 的 462 个文件与最终公共构建逐文件一致，且没有重复条目、缺失文件、额外文件、越界路径或内容哈希差异。正式 HTTPS `stable` 清单也已使用仓库固定 P-256 公钥重新验签；其 `releaseId` 仍为 `tablink-0.8.0`，Windows 与 Android 平台版本都仍为 `0.8.0`，本预览版没有推进稳定频道。
