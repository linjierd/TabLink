# 为 TabLink 做贡献

[English (Singapore)](CONTRIBUTING.md) | **简体中文**

欢迎参与贡献。请保持改动聚焦，清楚说明行为变化；如果改动涉及安全、设备身份、显示器所有权、传输帧格式或更新验证，请添加相应测试。

## 开发环境

- Windows 主机程序及其测试需要 Windows 11 x64 和 .NET 10 SDK。
- Android 客户端需要 JDK 17 和 Android SDK 35。
- 本地打包保留一份已签名的虚拟显示驱动包，但单元测试不会安装该驱动，也不会改变显示拓扑。
- ADB 和应用补丁的 FFmpeg 可执行文件是本地构建依赖，并被明确排除在 Git 之外。固定版本和哈希见 `third_party/adb/README.md` 与 `third_party/ffmpeg-tablink/README.md`。

在 PowerShell 中运行托管测试项目：

```powershell
$projects = Get-ChildItem .\tests -Filter '*.csproj' -Recurse
foreach ($project in $projects) {
    dotnet run --project $project.FullName -c Release
    if ($LASTEXITCODE -ne 0) { throw "Test failed: $($project.FullName)" }
}
```

在不安装驱动的情况下构建主机程序：

```powershell
dotnet build .\src\TabLink.Windows\TabLink.Windows.csproj -c Release
dotnet build .\src\TabLink.DriverSetup\TabLink.DriverSetup.csproj -c Release
```

## 隐私与硬件证据

切勿提交真实设备序列号、USB/PnP 实例 ID、配对令牌、私有 IP 地址、包含私钥的证书、含个人数据的截图，或贡献者电脑上的绝对路径。请在代码和测试中使用 `TEST-SERIAL-001` 等明显的测试值。原始设备诊断数据应保存在仓库之外。

## 兼容性目录

公开兼容性目录是经过整理的证据索引，不是遥测，也不是 Issue 或支持包的直接导出。每条记录只描述一个特定的 TabLink 版本、主机、接收端、传输方式和显示配置。必须区分请求刷新率、解码器提交帧、呈现回调和物理呈现测量。缺失的证据必须明确标记为未验证；不要把一项成功配置推广到整个设备系列。

只有维护者才应把经过审查、可公开且不具有唯一性的信息整理进 `compatibility/catalog.json`。切勿把 Issue 正文、ZIP 成员、附件名称或原始诊断输出复制进兼容性目录。校验器会拒绝未知字段和常见的身份、地址、路径与令牌模式，但自动检查无法证明型号标签可以公开，也无法证明测试结论属实，仍然必须人工审核。

修改兼容性目录后，请重新生成 schema 和 Markdown 视图，然后验证提交的输出与当前生成结果逐字节一致：

```powershell
dotnet run --project .\tools\TabLink.CompatibilityCatalog\TabLink.CompatibilityCatalog.csproj -c Release -- --root . --write
dotnet run --project .\tools\TabLink.CompatibilityCatalog\TabLink.CompatibilityCatalog.csproj -c Release -- --root . --check
```

该工具离线运行，不会打开 Issue、解压支持包、检查设备或访问网络。提交前请检查生成的差异。

## GitHub 语言

English (Singapore) 是 GitHub 的默认版本；对应的简体中文镜像必须保持完整。Issue 和 Pull Request 模板中的英文应排在中文之前。当前版本、下载、哈希、安全规则、隐私边界或尚未验证的限制发生变化时，必须在同一次提交中更新两种语言。提交前运行：

```powershell
.\tools\Test-GitHubLanguageContract.ps1
```

## 许可证

提交贡献即表示你同意：由你为 TabLink 创作的贡献按照本仓库的 MIT License 提供。第三方材料必须保留其原始许可证和来源记录，不得仅为了方便本地构建而复制进仓库。
