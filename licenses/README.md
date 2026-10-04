# Distribution license bundle

本目录保留分发所需的上游许可证和 notice 原文，来自固定版本的 NuGet 包、
.NET SDK/runtime 或 SPDX license-list-data，未改写条款。依赖用途与归属见
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)，分发规则见
[ADR 0015](../docs/adr/0015-github-unpackaged-setup-and-optional-local-runtime.md)。

## Core App / Setup

| 目录 | 固定来源 |
| --- | --- |
| `dotnet-runtime/` | .NET 10.0.11 runtime（SDK 10.0.303；原文哈希与上一固定副本一致） |
| `windows-app-sdk/` | Microsoft.WindowsAppSDK 2.3.1 |
| `webview2/` | Microsoft.Web.WebView2 1.0.3719.77 |
| `communitytoolkit-mvvm/` | CommunityToolkit.Mvvm 8.4.2 |
| `communitytoolkit-winui-notifications/` | CommunityToolkit.WinUI.Notifications 7.1.2 |
| `h-notifyicon/` | H.NotifyIcon.WinUI 2.4.1 及 H.NotifyIcon / H.GeneratedIcons.System.Drawing |
| `localsenddotnet-core/` | LocalSendDotNet.Core 0.2.0-preview.5 的 Apache-2.0 LICENSE / NOTICE |
| `fluent-ui-system-icons/` | Microsoft Fluent UI System Icons |
| `managed-dependencies/` | Microsoft MIT 原文；用于 Microsoft.Data.Sqlite、Recognizers Text、System.Drawing.Common 等 |
| `sqlite/` | SQLitePCLRaw.lib.e_sqlite3 3.53.3 的 public-domain 声明 |
| `sqlitepclraw/` | SPDX Apache-2.0 原文与 SQLitePCLRaw 2.1.11 包元数据 |

App 项目将这些文本及根目录的 `LICENSE.txt`、`THIRD-PARTY-NOTICES.md` 复制到
Release publish 和 Setup，构建脚本硬校验必需文本；核心应用采用 framework-dependent 部署。

## Optional local inference component

| 目录 | 固定来源 |
| --- | --- |
| `onnxruntime-genai/` | Microsoft.ML.OnnxRuntimeGenAI.Cuda 0.14.1 |
| `onnxruntime-gpu-windows/` | Microsoft.ML.OnnxRuntime.Gpu.Windows 1.26.0 |
| `onnxruntime-directml/` | Microsoft.ML.OnnxRuntime.DirectML 1.23.0 |

这些文本只随独立本地推理组件分发。模型权重和 NVIDIA 按需文件不在 Git 或核心
Setup 中；来源、许可证及精确哈希见应用清单和根第三方说明。版本或来源变化时须重新复核条款。

检查许可证 bundle 是否进入 publish：

```powershell
.\tools\release\Build-Setup.ps1 -Platform x64 -DryRun
```
