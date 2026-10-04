# ADR 0015：GitHub unpackaged 发行与可选本地推理组件

- 状态：Accepted
- 日期：2026-08-13
- 取代先前的 packaged/MSIX 发行决策
- 修订生产数据根、通知注册方式和本地推理组件部署位置

## 背景

PicForLater 只计划通过 GitHub Releases 以开源项目形式面向普通 Windows 用户发行，
不计划依赖 Microsoft Store。相当一部分用户只使用云端 API；若核心安装产物始终携带
ONNX Runtime、GenAI 和 CUDA/DirectML native runtime，所有用户都会承担显著的
额外下载。本项目接受未签名新版本可能出现的 SmartScreen 提示，不把代码签名
作为首次发布前提。

## 决策

1. 正式发行改为真正的 unpackaged WinUI 3。应用以
   `WindowsPackageType=None` 构建并从普通 `.exe` 启动，不生成或安装应用 MSIX，
   运行时不假定 package identity。
2. GitHub Release 为 x64 和 ARM64 分别提供在线 Setup、离线 Setup、对应的 Windows App
   Runtime ZIP、Visual C++ Runtime 安装器，以及按需本地分析所需的签名组件清单、detached
   signature 和组件 ZIP；不额外上传 checksum sidecar。在线 Setup 仅从同一 Release 下载
   不满足最低版本要求的 Windows App Runtime / Visual C++ 依赖；.NET Runtime 安装器由微软官方来源按需
   下载。用户仍从 GitHub 手动获取应用更新，不新增后台自动更新器。未签名状态和
   SmartScreen 预期必须在 README 与 Release 说明中如实披露。
3. 生产数据根固定为 `%LocalAppData%\PicForLater`。数据库、原图、缓存、staging、
   模型、设置和可选组件都位于该用户目录下，不放入安装目录；核心程序覆盖升级和普通
   卸载不得默认删除这些用户数据。Core 与 Infrastructure 继续只接收注入的绝对根路径。
4. 偏好设置改用 unpackaged 可用的持久化实现；API 密钥继续只进入 Windows 的当前
   用户安全凭据存储，不得回退为明文 JSON、注册表值或数据库字段。
5. 普通通知和定时提醒统一使用 `CommunityToolkit.WinUI.Notifications` 的
   `ToastNotificationManagerCompat` unpackaged desktop compatibility 路径；不再混用
   `AppNotificationManager` 激活与要求包身份的 `ToastNotificationManager.CreateToastNotifier()`。
   SQLite outbox 继续作为事实源，应用保持非提升权限运行。正常退出只退订激活事件；
   `Uninstall()` 只由 Setup 的卸载入口以 `--uninstall-notifications` 调用主 EXE。
6. ONNX Runtime、GenAI、CUDA/DirectML、PP-OCR/Qwen 执行实现和
   `PicForLater.LocalInference` worker 从 App 项目的编译与发布图中移除，形成架构专用的
   本地分析组件。核心应用只保留版本化管道 client、远端 Provider、SQLite 作业和可在
   主进程执行的轻量 Windows OCR/图片清理能力。
7. 本地组件安装到 `%LocalAppData%\PicForLater\components\local-inference\<arch>\<version>`。
   client 只从经过验证并原子激活的版本目录启动 worker；缺失、架构错误、协议不兼容或
   校验失败时，本地模式明确不可用，不静默切换到远端 API。
   `component.json` 的逐文件 SHA-256 只用于安装后完整性检查，不能独立证明发布来源；
   一键下载必须先验证由 App 内置信任根认证的外层 release manifest，再解压、复验并
   原子切换 `active.json`。在信任根和稳定 Release URL 确认前，不启用可执行组件下载。
8. 核心采用 `.NET framework-dependent + Windows App SDK framework-dependent`。在线
   Setup 检查兼容的 `Microsoft.NETCore.App` 和 `Microsoft.AspNetCore.App` 10.0 runtime；
   仅在缺少满足要求的版本时从微软官方来源获取相应运行时 EXE，最终用户不需要安装
   .NET SDK。Windows App Runtime 与 Visual C++ 运行库也只在缺少满足要求的版本时安装：在线 Setup 从当前 GitHub Release
   获取架构专用 Windows App Runtime ZIP 与 VC redist；离线 Setup 则包含完整前置运行库，
   不需要网络。下载载荷由各自 manifest 锁定；.NET 最低版本从实际发布的 runtimeconfig
   推导，兼容的已安装服务补丁版本直接复用，并核对 ASP.NET Core 自身要求的基础 .NET 版本。
   在线载荷使用系统 .NET 网络库按 8 段并发下载，显示总进度与速度，并允许停止下载；服务器
   不支持 Range 时复用单连接响应。分段逐一检查响应范围与长度，合并后复用安装前的完整
   SHA-256 校验，每个载荷只计算一次；长度或 Range 正确不能证明内容一致。失败或取消时
   清理临时分段，不保留断点。已完成 GitHub 重定向的地址供剩余分段复用，避免重复跳转。
   构建脚本校验微软运行时载荷的长度、SHA-256 和 Authenticode 签名，并以
   `--quiet --msix` 为当前用户注册适用的 framework、Main、Singleton 和 DDLM 包；PicForLater
   本身始终不注册为 MSIX。
9. Setup 是 per-user、`PrivilegesRequired=lowest` 且不允许提升覆盖，默认安装到
   `%LocalAppData%\Programs\PicForLater`。安装器保持应用安装为 per-user；补装 .NET、
   ASP.NET Core 或 VC redist 时按各自安装要求请求管理员授权，Windows App Runtime 则
   注册给当前用户。完成运行库检测后才复制应用文件；创建开始菜单快捷方式并提供可选
   桌面快捷方式；覆盖安装复用目录和任务选择；卸载删除程序、快捷方式、通知注册和
   卸载项，但保留 `%LocalAppData%\PicForLater` 用户数据。
   前置安装返回 3010 后先复检可用性，依赖已就绪则继续安装应用；只有对应组件仍不可用时
   才要求重启后继续。1641 表示已启动重启，仍立即停止安装。此流程不清除系统待重启记录。
   准备阶段使用内置 marquee 页面显示当前组件和等待时间，等待子进程时保持窗口响应，
   隐藏导航与取消按钮，避免在运行库安装中途切换步骤。临时脚本与运行库载荷放在应用文件
   之前；已压缩的 EXE/ZIP 不再重复压缩，应用文件继续 solid 压缩。安装器自动记录临时
   Setup 日志中的步骤、耗时和退出码，用于区分检测、验签和系统安装的等待。
10. Release publish 必须包含同一次 WinUI 构建生成的 `PicForLater.App.pri` 和全部 XBF。
    构建脚本对 PRI、关键 XBF、主 EXE 和禁止的本地推理文件执行硬校验，避免生成可安装
    但在 WinUI 启动期崩溃的残缺布局。
11. 本地组件外层发布清单采用 RSA-PSS/SHA-256 detached signature；App 只接受内置
    公钥验证通过的原始 JSON 字节。清单锁定组件 ID、架构、协议、压缩与解压体积、ZIP
    SHA-256 和 `component.json` SHA-256。下载器逐跳限制为 PicForLater 官方 GitHub
    Release 与 GitHub release-assets 域，不携带 Cookie；解压后还须通过路径、条目数、
    reparse point、逐文件哈希和清单外文件检查，全部通过后才在短维护窗口内原子激活。
12. 清单签名验证失败时，应用拒绝安装或激活组件，不能降级接受未签名 ZIP。
13. 应用在 XAML 初始化前通过 Windows App SDK `AppInstance` 注册稳定实例键。同一测试或
    生产通道的后续启动只把激活重定向到主进程并退出；主进程恢复最小化窗口并请求前台，
    不创建第二个 `Application` 或 `MainWindow`。窗口关闭时注销实例键，允许下一次启动
    正常成为主实例；Setup 的 `--uninstall-notifications` 维护入口不参与实例重定向。

## 迁移与兼容

- 现有数据库 schema、相对路径、作业、隐私边界、worker 管道协议和 45 秒空闲退出保持
  不变。部署迁移不得借机重写业务分层或扩大网络行为。

## 结果

- 纯云端用户的核心下载可以不再包含 ONNX/CUDA payload；本地用户在明确启用时再下载
  对应架构组件和模型。
- 新机器首次安装时只下载缺失的运行库；已有兼容运行库时，后续应用更新无需重复下载。
  离线 Setup 可用于无法联网的首次安装或维护。
- Windows 不再提供应用 MSIX 的安装、更新、回滚、完整性和卸载生命周期，这些责任由
  Setup、组件安装器和应用内校验共同承担。
- 每个未签名新版本可能重新触发 SmartScreen 文件信誉提示；企业策略或 Smart App
  Control 仍可能阻止运行。GitHub 托管和源码公开不等同于 Windows 发布者信任。
- 组件清单签名只认证可选执行组件的发布来源，不等同于 Authenticode，也不会建立
  `Setup.exe` 的 SmartScreen 发布者信誉。
