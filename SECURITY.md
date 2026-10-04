# Security Policy

## Supported versions

在首次正式 Release 之前，本项目没有受支持的公开二进制版本。

正式发布后，默认仅对 GitHub Releases 中的**最新版本**提供安全修复。若支持范围发生变化，将在本文件或对应的 Release 说明中更新。

## Reporting a vulnerability

请通过 **zhdds@protonmail.com** 私密报告漏洞。请勿公开密钥、私人图片、数据库内容或完整远程请求/响应；修复或协调披露前，也请勿在公开 Issue、Discussion、日志或附件中发布漏洞细节或利用代码。

报告中请避免包含与漏洞分析无关的真实用户数据，并尽可能提供：

* 受影响的版本；
* 漏洞的影响范围；
* 必要的复现步骤或最小复现条件；
* 相关错误信息或日志片段（请先移除 API key、访问令牌、个人路径及其他敏感信息）；
* 如适用，可附上缓解建议。

维护者会尽力确认问题、评估影响并协调披露，不承诺固定响应或修复 SLA。修复后可在 Release notes 或安全公告中说明处理情况。

## Release authenticity

请仅从[官方 GitHub Releases](https://github.com/dogdreamson555/PicForLater/releases) 获取发布文件。

首版 `Setup.exe` 及应用程序**未进行 Authenticode 代码签名**，因此 Windows SmartScreen、防病毒软件或组织安全策略可能显示警告或阻止运行。这种警告本身不能用于证明文件是否来自本项目。

源码公开、文件托管于 GitHub，以及某些文件可能提供的 detached signature，均**不等同于 Windows Authenticode 代码签名**。

如未来开始对 Windows 可执行文件进行代码签名，相关验证方式将另行在本文件或 Release 说明中公布。
