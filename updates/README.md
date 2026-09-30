# TabLink 正式稳定版更新信任与发布

TabLink 的稳定更新清单使用 ECDSA P-256 / SHA-256 签名。`stable-public-key.spki.base64`
是所有客户端固定信任的 X.509 SubjectPublicKeyInfo 公钥，文件 SHA-256 为：

`B275488873764E84A4FA3252D7EFAAFCBD13B97DB95A4D43C3F852748C12FECA`

与其配对的私钥位于源代码树外，默认路径是：

`%LOCALAPPDATA%\TabLink\release-signing\stable-private.pem`

私钥不得提交、复制到发布目录或打进安装包。若私钥丢失，已安装客户端无法信任用新密钥签发的清单；密钥轮换必须先发布一个同时内置新公钥的过渡客户端。

## 清单格式

`manifest.json` 是一个只有两个小写属性的 JSON 对象：

```json
{
  "payload": "<标准 Base64 编码的原始 UTF-8 payload.json 字节>",
  "signature": "<标准 Base64 编码的 ASN.1 DER ECDSA 签名>"
}
```

签名输入是 `payload` 解码后的**原始字节**，不能重新排版 JSON 后再验签。客户端必须先用固定公钥验签，成功后才解析负载字段。负载遵循
[`manifest-v1.schema.json`](manifest-v1.schema.json)，并有这些额外约束：

- `channel` 必须是 `stable`；版本必须是纯 `major.minor.patch`，不能含预发布或构建后缀。
- `publishedAtUtc` 必须严格为整秒 UTC：`yyyy-MM-dd'T'HH:mm:ss'Z'`。
- 每个平台最多一个制品；URL 必须是 HTTPS，可含下载服务所需的 query，但不能含账号信息或 fragment。
- `rolloutPercentage` 接受 `0..100`。`0` 是已签名的紧急暂停：客户端仍验证并接受清单，但不选择、不下载该版本，并清除尚未安装的本地待办；恢复时发布新的已签名清单并将比例调回 `1..100`。
- 下载后必须同时核对 `size` 和 `sha256`，然后才交给平台安装流程。

`TabLink.ReleaseTool` 在签名和验签时都会检查上述结构；验签路径会在解析不可信负载前先检查签名。

## 只暂存、不上传

下面的命令构造 Windows ZIP，计算大小与 SHA-256，生成紧凑负载，用仓库外私钥签名，再用公开密钥验签。它不会连接服务器或上传文件：

```powershell
.\tools\New-TabLinkStableRelease.ps1 `
  -Version '0.8.0' `
  -WindowsBuild 800 `
  -BaseUrl 'https://updates.example.com/tablink' `
  -WindowsSource '.\dist\TabLink'
```

如使用现有 DownloadSite query 下载接口，把 `{path}` 作为唯一 query 值模板：

```powershell
.\tools\New-TabLinkStableRelease.ps1 `
  -Version '0.8.0' `
  -WindowsBuild 800 `
  -BaseUrl 'https://linjie.space/download/api/download?path={path}' `
  -WindowsSource '.\dist\TabLink'
```

脚本会将 `TabLink/stable/0.8.0/TabLink-windows-x64-0.8.0.zip` 作为一个完整值用
`Uri.EscapeDataString` 编码后替换 `{path}`，不会拼接任意命令或额外 query。稳定清单的下载路径对应
`TabLink/stable/manifest.json`。

Windows 正式包在签名前必须通过完整性预检：四个 `TabLink.Updater` 文件、`selftest-result.txt`、
`SHA256SUMS.txt` 和 `update-channel.json` 都必须存在；更新频道必须启用且精确指向
`https://linjie.space/download/api/download?path=TabLink%2Fstable%2Fmanifest.json`。暂存脚本逐项重新计算
`SHA256SUMS.txt` 中列出的文件，不接受缺失文件、越界路径、重复路径或哈希不一致。构建正式候选时，显式
`-OutputDirectory` 必须是尚不存在或完全为空的目录，避免旧版本文件进入签名 ZIP。

若同时暂存 Android APK，必须提供 APK 内的 `versionCode` 和正式签名证书 SHA-256 指纹：

```powershell
.\tools\New-TabLinkStableRelease.ps1 `
  -Version '0.8.0' `
  -WindowsBuild 800 `
  -BaseUrl 'https://linjie.space/download/api/download?path={path}' `
  -WindowsSource '.\dist\TabLink' `
  -AndroidApk '.\android\artifacts\TabLink-android-0.8.0.apk' `
  -AndroidBuild 11 `
  -AndroidSignerSha256 '<正式 APK 签名证书的 64 位十六进制 SHA-256>'
```

脚本会使用 `aapt` 读取 APK 的 `versionName/versionCode`，使用 `apksigner` 验证 APK 并核对指定证书指纹。未显式钉住 Android 签名者时，APK 不会进入稳定发布。

默认输出在被 Git 忽略的 `artifacts\stable\<version>`：

```text
<version>/
  release/
    TabLink-windows-x64-<version>.zip
    TabLink-android-<version>.apk       # 可选
  payload.json
  manifest.json
  SHA256SUMS.txt
  release-summary.json
```

版本目录一次性创建。目标版本已存在时脚本直接失败；全部校验成功后才把临时目录原子移动到最终目录。Windows ZIP 内文件顺序和时间戳固定，同一组输入会得到相同 ZIP 字节。`release-summary.json` 明确记录 `uploaded=false` 和
`uploadPerformedByTool=false`。

实际发布时，先把 `release` 内不可变制品上传到清单列出的版本 URL，并从 HTTPS 端重新核对大小和 SHA-256；最后才把验签通过的 `manifest.json` 原子发布到 `stable/manifest.json`。这一步由部署流程执行，不属于暂存脚本。

## 工具命令与测试

直接操作签名工具：

```powershell
dotnet run --project .\tools\TabLink.ReleaseTool -- init-key <private.pem> <public.spki.base64>
dotnet run --project .\tools\TabLink.ReleaseTool -- sign <private.pem> <payload.json> <manifest.json>
dotnet run --project .\tools\TabLink.ReleaseTool -- verify <public.spki.base64> <manifest.json>
```

`init-key`、`sign` 都使用新建文件语义，不覆盖现有密钥或清单。运行完整负向测试：

```powershell
.\tools\TabLink.ReleaseTool\Test-ReleaseTool.ps1
```

测试使用临时生成的测试密钥，并覆盖正常签名/验签、小写外层字段、HTTPS query 互操作、负载篡改、签名篡改、私钥缺失、目标已存在、HTTP URL 和带小数秒时间戳。测试产物只在系统临时目录中创建，结束后删除。
