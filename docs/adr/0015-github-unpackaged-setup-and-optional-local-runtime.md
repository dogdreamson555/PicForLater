# ADR 0015：GitHub unpackaged 发行与可选本地推理组件

- 状态：Accepted
- 日期：2026-08-13
- 取代先前的 packaged/MSIX 发行决策，修订数据根、通知注册和本地组件部署位置

## 背景

PicForLater 通过 GitHub Releases 面向 Windows 用户发行，不依赖 Microsoft Store。
纯云端用户无需承担 ONNX Runtime、GenAI 和 CUDA/DirectML 的下载体积，因此将本地
推理拆为可选组件。首次发布不以代码签名为前提，须如实披露未签名版本的 SmartScreen 提示。

## 决策

1. 应用采用真正的 unpackaged WinUI 3，以 `WindowsPackageType=None` 构建并从普通
   EXE 启动，不生成应用 MSIX，也不假定 package identity。
2. 每次 GitHub Release 为 x64、ARM64 提供在线/离线 Setup、Windows App Runtime ZIP、
   VC redist，以及可选本地组件的签名清单、detached signature 和 ZIP；不额外上传
   checksum sidecar。应用更新由用户手动获取，不新增后台自动更新器；README 和
   Release 说明必须披露未签名状态及 SmartScreen 预期。
   官方 Setup 由 GitHub Actions 使用同次 Release publish 产物生成；CI 与发布流程只使用
   GitHub 官方 Actions，并固定到完整 commit SHA。
3. 数据根固定为 `%LocalAppData%\PicForLater`，容纳数据库、原图、缓存、staging、
   模型、设置和可选组件。覆盖升级及普通卸载保留数据；Core 与 Infrastructure 只接收
   注入的绝对根路径。偏好设置使用 unpackaged 可用的存储，API 密钥只进入当前 Windows
   用户的安全凭据存储，不得回退到明文 JSON、注册表或数据库。
4. 普通通知和定时提醒统一使用 `ToastNotificationManagerCompat` 的 unpackaged
   兼容路径，不混用 `AppNotificationManager` 或要求包身份的
   `ToastNotificationManager.CreateToastNotifier()`。SQLite outbox 是事实源，应用不提升
   权限；正常退出只退订激活事件，`Uninstall()` 仅由 Setup 以
   `--uninstall-notifications` 调用主 EXE。
5. 核心采用 .NET 与 Windows App SDK 的 framework-dependent 部署。Setup 复用兼容的
   `Microsoft.NETCore.App`、`Microsoft.AspNetCore.App` 10.0、Windows App Runtime 和
   VC redist，仅补齐缺失或低于最低要求的运行库，最终用户不需要 .NET SDK。
   在线 Setup 从同一 Release 获取架构匹配的 Windows App Runtime ZIP / VC redist，
   从微软官方来源获取 .NET / ASP.NET Core EXE；离线 Setup 包含全部前置运行库，无需网络。
   载荷由 manifest 锁定；.NET 最低版本由发布的 runtimeconfig 推导，并核对 ASP.NET Core
   对基础 .NET 的要求。前置载荷校验长度和 SHA-256；安装 EXE 另验 Authenticode，
   Windows App Runtime ZIP 内的 MSIX 另验微软签名与包身份，并以 `Add-AppxPackage`
   注册当前用户的 framework、Main、Singleton、DDLM 包，应用本身仍不注册 MSIX。
6. 在线载荷使用系统 .NET 网络库：支持 Range 时按 8 段并发下载，显示总进度和速度并可
   停止；不支持 Range 时复用单连接响应。逐段核对响应范围与长度，合并后每个载荷只做
   一次安装前的完整 SHA-256 校验；长度或 Range 正确不能代替内容校验。其余分段复用已
   完成 GitHub 重定向的地址；失败或取消清理临时分段，不保留断点。
7. Setup 以 per-user、`PrivilegesRequired=lowest` 安装到
   `%LocalAppData%\Programs\PicForLater`，不允许提升覆盖；补装 .NET、ASP.NET Core 或
   VC redist 时按其要求请求管理员授权；VC redist 是机器级安装，Windows App Runtime 注册给当前用户。
   前置运行库就绪后才复制应用，创建开始菜单及可选桌面快捷方式；覆盖安装复用目录和任务
   选择，卸载移除程序、快捷方式、通知注册和卸载项，保留用户数据。
   返回 3010 后复检依赖，已就绪则继续，否则要求重启；1641 已启动重启，立即停止安装，
   不清除系统待重启记录。等待子进程时保持窗口响应并显示组件/耗时；运行库安装期间锁定
   导航和取消，临时 Setup 日志记录步骤、耗时与退出码。
8. ONNX Runtime、GenAI、CUDA/DirectML、PP-OCR/Qwen 实现和本地 worker 从核心 App
   编译/发布图移出；核心只保留版本化管道 client、远端 Provider、SQLite 作业，以及轻量
   Windows OCR/图片清理。组件安装到
   `%LocalAppData%\PicForLater\components\local-inference\<arch>\<version>`：
   - 外层 release manifest 使用 RSA-PSS/SHA-256 detached signature，App 只接受内置
     公钥验证通过的原始 JSON 字节；信任根和稳定 Release 来源确认前，不启用组件下载。
   - 清单锁定组件 ID、架构、协议、压缩/解压体积、ZIP 和 `component.json` 的 SHA-256。
     下载逐跳限制为官方 GitHub Release / release-assets 域，不携带 Cookie。
   - 解压后复验路径、条目数、reparse point、逐文件哈希及清单外文件。下载与复验完成后
     才进入 worker 维护阶段：等待当前操作结束、停止 worker，并在维护租约内原子切换
     `active.json`。文件哈希不能独立证明发布来源，验签失败不能降级接受未签名 ZIP。
   - client 只从验证并激活的版本目录启动 worker；缺失、架构/协议不匹配或校验失败时，
     本地模式明确不可用，不静默切换远端 API。
9. Release publish 必须包含同次 WinUI 构建的主 EXE、`PicForLater.App.pri` 和全部 XBF；
   构建脚本校验必需资源及禁止的本地推理文件，拒绝可安装但无法启动的残缺布局。
10. XAML 初始化前通过 `AppInstance` 注册稳定实例键。同一测试或生产通道的后续启动只
    重定向激活并退出；主进程恢复窗口并请求前台，不创建第二个 Application/MainWindow。
    窗口关闭时注销实例键；`--uninstall-notifications` 维护入口不参与重定向。

## 迁移与兼容

保留数据库 schema、相对路径、作业、隐私边界、管道协议和 worker 的 45 秒空闲退出。
部署迁移不重写业务分层或扩大网络行为。

## 结果

- 云端用户无需下载本地推理资源；本地用户明确启用后再安装对应架构的组件和模型。
- 首次在线安装只补齐缺失运行库，兼容环境下更新无需重复下载；离线 Setup 支持无网络安装。
- 应用 MSIX 原有的安装、更新、回滚、完整性和卸载责任由 Setup、组件安装器及应用校验承担。
- 每个未签名版本可能触发 SmartScreen，企业策略或 Smart App Control 仍可能阻止运行。
  GitHub 托管、源码公开及组件清单签名均不建立 Setup 的 Authenticode 身份或 SmartScreen 发布者信誉。
