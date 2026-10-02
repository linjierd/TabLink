# TabLink 设备兼容性目录

[English (Singapore)](README.md) | **简体中文**

此目录由 `compatibility/catalog.json` 自动生成，请勿手工编辑。Schema v1，当前 2 条记录。
只收录经过人工审查的非唯一设备型号和能力信息。
每条记录只证明表中完全相同的软件、硬件和连接配置；不能据此推断同型号的其他系统版本或连接方式。

请求刷新率不等于解码提交、呈现回调或物理呈现帧率；未测量的物理呈现显示为 `—`。
目录不接受自动遥测、Issue 或支持包自动导入、设备序列号、网络地址、USB 标识符或配对凭据。自动校验不能代替人工确认公开型号与测试结论。
修改源数据后运行 `dotnet run --project tools/TabLink.CompatibilityCatalog/TabLink.CompatibilityCatalog.csproj -c Release -- --root . --write`，并提交源数据、Schema 和本页的两个语言版本。

## 已审核记录

| 日期 | 结果 | 电脑 | 接收设备 | 连接 | 显示 | 刷新率 | 视频 | 证据 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2026-10-02 | `verified` | Windows 11 / `x64` / NVIDIA RTX 4060 Laptop GPU | `android` / `android-native` / ZTE W202DS / Android 13 / c2.unisoc.avc.decoder | `native-network` | 1920×1200（原生 1200×1920，旋转 1/4 圈） | 90 Hz（请求 90 Hz；支持 60/90 Hz） | `h264` / `nvenc` / `hardware`；呈现回调 90 fps；物理呈现 — | [VERIFICATION-0.8.8.md](../VERIFICATION-0.8.8.md) |
| 2026-10-02 | `verified` | Windows 11 / `x64` / NVIDIA RTX 4060 Laptop GPU | `android` / `android-native` / ZTE W202DS / Android 13 / unknown | `adb` | 1920×1200（原生 1200×1920，旋转 1/4 圈） | 90 Hz（请求 90 Hz；支持 60/90 Hz） | `h264` / `nvenc` / `unknown`；呈现回调 90.004 fps；物理呈现 86.966 fps (`surfaceflinger`) | [VERIFICATION-0.8.9.md](../VERIFICATION-0.8.9.md) |

## 能力与限制

### `tlc-000001`

- TabLink：`0.8.8` / `preview` / `v0.8.8-preview.1`
- 来源：`maintainer-verification`，提交 `7c20a72c5d77cd454a4115d4a763d668f002f329`
- 视频测量：请求 90 fps；有效 90 fps；提交 90.1 fps；呈现回调 90 fps；物理呈现 —
- 已验证：`app-process-restart-reconnect`、`extended-desktop`、`hardware-decoding`、`native-orientation`、`ninety-hz`、`single-display-cleanup`、`trusted-reconnect`、`trusted-registration`
- 限制：`active-revocation-not-tested`、`route-migration-not-tested`、`system-restart-not-tested`、`token-replay-not-tested`

### `tlc-000002`

- TabLink：`0.8.9` / `preview` / `v0.8.9-preview.1`
- 来源：`maintainer-verification`，提交 `2a1c3aced048315e3171489fc410c6adeb2eed66`
- 视频测量：请求 90 fps；有效 90 fps；提交 89.952 fps；呈现回调 90.004 fps；物理呈现 86.966 fps (`surfaceflinger`)
- 已验证：`extended-desktop`、`native-orientation`、`ninety-hz`、`single-display-cleanup`
- 限制：`system-restart-not-tested`、`usb-debug-required`
