# Security Policy

## Supported versions

在首次正式 Release 之前，本项目没有受支持的公开二进制版本。

正式发布后，默认仅对 GitHub Releases 中的**最新版本**提供安全修复。若支持范围发生变化，将在本文件或对应的 Release 说明中更新。

## Reporting a vulnerability

如果你发现了潜在的安全漏洞，请通过以下邮箱进行私密报告：

**Security contact:** zhdds@protonmail.com

请**不要**通过公开 GitHub Issue、Discussion、日志或附件披露尚未修复的安全漏洞或敏感信息。

报告中请避免包含与漏洞分析无关的真实用户数据，并尽可能提供：

* 受影响的版本；
* 漏洞的影响范围；
* 必要的复现步骤或最小复现条件；
* 相关错误信息或日志片段（请先移除 API key、访问令牌、个人路径及其他敏感信息）；
* 如适用，可附上缓解建议。

请勿在公开渠道提交 API key、访问令牌、私人图片、数据库内容、完整的远程请求或响应、可直接用于攻击的利用代码，或尚未修复漏洞的详细复现信息。

收到报告后，维护者会尽力确认问题、评估影响并与报告者协调后续披露。在正式公布响应时限之前，本项目不承诺固定的响应或修复 SLA。

如果报告内容涉及可立即被滥用的漏洞，请在问题修复或协调披露之前保持相关技术细节私密。

## Public disclosure

为了给修复和发布安全更新留出合理时间，请不要在问题得到处理之前公开尚未修复漏洞的完整技术细节。

漏洞修复完成后，维护者可根据问题的严重程度，在 Release notes、安全公告或其他适当渠道中说明修复情况。

## Release authenticity

请仅从本项目官方 GitHub Releases 页面获取发布文件：

https://github.com/dogdreamson555/PicForLater/releases

首版 `Setup.exe` 及应用程序**未进行 Authenticode 代码签名**，因此 Windows SmartScreen、防病毒软件或组织安全策略可能显示警告或阻止运行。这种警告本身不能用于证明文件是否来自本项目。

源码公开、文件托管于 GitHub，以及某些文件可能提供的 detached signature，均**不等同于 Windows Authenticode 代码签名**。

如未来开始对 Windows 可执行文件进行代码签名，相关验证方式将另行在本文件或 Release 说明中公布。
