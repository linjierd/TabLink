# 安全策略

[English (Singapore)](SECURITY.md) | **简体中文**

TabLink 涉及管理员权限提升、显示驱动、USB 设备身份、TLS 配对、输入转发和签名软件更新。请勿在公开 Issue 中提交漏洞利用方法，也不要提交任何设备序列号、USB/PnP ID、IP/MAC 地址、本机或网络路径、配对链接、令牌、证书、密钥、原始日志或私有诊断捕获内容。

请通过仓库的 **Security** 标签页，使用[私密 GitHub Security Advisory](https://github.com/linjierd/TabLink/security/advisories/new)报告安全漏洞。请说明受影响的版本、最小复现步骤和预期的安全边界。请用明显的占位符替换私密值。如果测试过程中暴露了令牌、证书或密钥，请轮换相应材料，并且不要在报告中包含原始值。

应用内的 v1 脱敏支持包只包含预览中显示的结构化字段，不会读取或复制原始日志。TabLink 从不会自动发送、上传或附加支持包。只有在你主动选择并检查过支持包内容后才可附加；支持包是可选的，提交公开 Issue 或 Security Advisory 均不强制要求提供。

安全修复仅面向最新发布版本。历史本地候选版本和未签名的开发版 APK 不视为受支持的稳定版本。
