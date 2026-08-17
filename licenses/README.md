# Distribution license bundle

本目录保留 PicForLater 源码及二进制分发会用到的上游许可证和 notice 原文。文件直接复制自固定版本的已还原 NuGet 包、固定 .NET SDK/runtime，或 SPDX 官方 license-list-data；没有改写上游条款。

## Core App / Setup

| 目录 | 固定来源 | 用途 |
| --- | --- | --- |
| `dotnet-runtime/` | 本地 .NET 10.0.10 runtime（SDK 10.0.302 安装） | self-contained .NET runtime 许可证及第三方 notices |
| `windows-app-sdk/` | `Microsoft.WindowsAppSDK` 2.3.1 | WinUI/Windows App SDK Runtime 条款与 notices |
| `webview2/` | `Microsoft.Web.WebView2` 1.0.3719.77 | Windows App SDK 的传递依赖条款与 notices；应用不直接托管 WebView |
| `communitytoolkit-mvvm/` | `CommunityToolkit.Mvvm` 8.4.2 | MVVM 运行库许可证及 notices |
| `communitytoolkit-winui-notifications/` | `CommunityToolkit.WinUI.Notifications` 7.1.2 | unpackaged 通知兼容层许可证 |
| `fluent-ui-system-icons/` | Microsoft Fluent UI System Icons | 应用图标 SVG 组合及其 PNG/ICO 派生文件的 MIT 许可证 |
| `managed-dependencies/` | Microsoft MIT license text | Microsoft.Data.Sqlite、Recognizers Text、System.Drawing.Common 等 MIT managed dependencies |
| `sqlite/` | `SQLitePCLRaw.lib.e_sqlite3` 3.53.3 | 捆绑 SQLite 的 public-domain 声明 |
| `sqlitepclraw/` | SPDX `Apache-2.0.txt` + `SQLitePCLRaw` 2.1.11 package metadata | SQLitePCLRaw Apache-2.0 条款；版权归属见根第三方 notices |

以上核心目录由 App 项目复制到 Release publish，并最终进入 Setup。`LICENSE.txt` 与 `THIRD-PARTY-NOTICES.md` 同时进入发布目录。

## Optional local inference component

| 目录 | 固定来源 |
| --- | --- |
| `onnxruntime-genai/` | `Microsoft.ML.OnnxRuntimeGenAI.Cuda` 0.14.1 |
| `onnxruntime-gpu-windows/` | `Microsoft.ML.OnnxRuntime.Gpu.Windows` 1.26.0 |
| `onnxruntime-directml/` | `Microsoft.ML.OnnxRuntime.DirectML` 1.23.0 |

这些文本只随独立的可选本地推理组件复制；核心 App/Setup 不携带 ONNX Runtime、GenAI、CUDA/DirectML 二进制。模型权重和 NVIDIA 按需文件不在 Git 或核心 Setup 中，其来源、许可证和精确哈希由应用内清单及 `THIRD-PARTY-NOTICES.md` 记录。

## Review result

- PicForLater 源码：MIT。
- 核心可再分发二进制：已为固定版本保留相应条款/notice，并由 publish 硬校验其存在。
- Inno Setup：只在构建机上使用，编译器不进入仓库或安装目录；生成的 Setup 保留 Inno 自带标识。
- GitHub Actions：只使用 GitHub 官方 actions，并固定到完整 commit SHA。
- 测试猫图：由仓库所有者确认可按 Unsplash License 再分发，固定哈希记录在根第三方 notices。
- 未发现要求将本项目改为非 MIT、禁止当前二进制分发或必须随核心 Setup 携带模型权重的条款。第三方服务政策和按需模型许可证仍需在版本/来源变化时重新复核。

可用以下命令检查 bundle 是否在 publish 中：

```powershell
.\tools\release\Build-Setup.ps1 -Platform x64 -DryRun
```
