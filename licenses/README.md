# Distribution license bundle

This directory retains the original upstream license and notice texts required for
redistribution, from pinned NuGet packages, the .NET SDK/runtime, or SPDX
license-list-data. Their terms have not been rewritten. See
[THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) for dependency purposes and
attribution, and [ADR 0015](../docs/adr/0015-github-unpackaged-setup-and-optional-local-runtime.md)
for distribution rules.

## Core App / Setup

| Directory | Pinned source |
| --- | --- |
| `dotnet-runtime/` | .NET 10.0.11 runtime (SDK 10.0.303; original text hashes match the previous pinned copy) |
| `windows-app-sdk/` | Microsoft.WindowsAppSDK 2.3.1 |
| `webview2/` | Microsoft.Web.WebView2 1.0.3719.77 |
| `communitytoolkit-mvvm/` | CommunityToolkit.Mvvm 8.4.2 |
| `communitytoolkit-winui-notifications/` | CommunityToolkit.WinUI.Notifications 7.1.2 |
| `h-notifyicon/` | H.NotifyIcon.WinUI 2.4.1 and H.NotifyIcon / H.GeneratedIcons.System.Drawing |
| `localsenddotnet-core/` | Apache-2.0 LICENSE / NOTICE from LocalSendDotNet.Core 0.2.0-preview.5 |
| `fluent-ui-system-icons/` | Microsoft Fluent UI System Icons |
| `managed-dependencies/` | Original Microsoft MIT text for Microsoft.Data.Sqlite, Recognizers Text, System.Drawing.Common, and others |
| `sqlite/` | Public-domain statement from SQLitePCLRaw.lib.e_sqlite3 3.53.3 |
| `sqlitepclraw/` | Original SPDX Apache-2.0 text and SQLitePCLRaw 2.1.11 package metadata |

The App project copies these texts, along with the root `LICENSE.txt` and
`THIRD-PARTY-NOTICES.md`, into Release publish output and Setup. Build scripts
strictly validate the required texts. The core app uses framework-dependent deployment.

## Optional local inference component

| Directory | Pinned source |
| --- | --- |
| `onnxruntime-genai/` | Microsoft.ML.OnnxRuntimeGenAI.Cuda 0.14.1 |
| `onnxruntime-gpu-windows/` | Microsoft.ML.OnnxRuntime.Gpu.Windows 1.26.0 |
| `onnxruntime-directml/` | Microsoft.ML.OnnxRuntime.DirectML 1.23.0 |

These texts are distributed only with the separate local inference component.
Model weights and NVIDIA files downloaded on demand are not included in Git or
core Setup. Sources, licenses, and exact hashes are recorded in the app manifests
and the root third-party notices. Terms must be reviewed again whenever a version
or source changes.

Check whether the license bundle is included in publish output:

```powershell
.\tools\release\Build-Setup.ps1 -Platform x64 -DryRun
```
