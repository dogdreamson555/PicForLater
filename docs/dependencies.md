# Dependency register

Primary direct and release-relevant transitive dependencies are listed below.
Attribution and terms are in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md);
fixed license texts are mapped in [licenses/README.md](../licenses/README.md).

| Dependency | Version | Purpose | Distribution |
| --- | --- | --- | --- |
| Microsoft.WindowsAppSDK | 2.3.1 | Unpackaged WinUI 3 | Core; runtime registered per-user by Setup |
| Microsoft.Web.WebView2 | Transitive | Windows App SDK integration; no app-hosted WebView | Core transitive dependency |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.8249 | Windows SDK build tooling | Build only |
| Microsoft.Windows.SDK.BuildTools.WinApp | 0.4.0 | Windows app build tooling | Build only |
| CommunityToolkit.Mvvm | 8.4.2 | Observable properties and commands | Core |
| CommunityToolkit.WinUI.Notifications | 7.1.2 | Unpackaged notifications | Core |
| H.NotifyIcon.WinUI | 2.4.1 | Tray icon and native PopupMenu | Core |
| LocalSendDotNet.Core | 0.2.0-preview.5 | Independent LocalSend v2.2-compatible LAN receiver | Core |
| Microsoft.NETCore.App | .NET 10 framework reference | Framework-dependent app runtime | Setup prerequisite |
| Microsoft.AspNetCore.App | .NET 10 framework reference | Kestrel HTTPS for the LAN receiver | Setup prerequisite |
| System.Drawing.Common | 10.0.11 | Security override for an older transitive version | Core |
| Microsoft.Recognizers.Text.DateTime | 1.8.13 | Local date/time candidates | Core |
| NuGet.CommandLine | 7.6.0 (`PrivateAssets=all`) | Security override for an obsolete build dependency | Build only; tools not published |
| Microsoft.Data.Sqlite | 10.0.10 | SQLite access and migrations | Core |
| SQLitePCLRaw.lib.e_sqlite3 | 3.53.3 | Patched native SQLite | Core |
| Microsoft Visual C++ Redistributable | [Pinned manifest](../tools/release/setup/visual-cpp-runtime.json) | Native runtime dependencies | Machine-wide; installed only if required |
| Microsoft.ML.OnnxRuntimeGenAI.Cuda | 0.14.1 | x64 Qwen/PP-OCR worker | Optional local component |
| Microsoft.ML.OnnxRuntime.Managed / Microsoft.ML.OnnxRuntime.Gpu.Windows | 1.26.0 | x64 CUDA/CPU runtime | Optional local component |
| Microsoft.ML.OnnxRuntimeGenAI.DirectML | 0.14.1 | ARM64 DirectML/CPU worker | Optional local component |

## Runtime deployment

[ADR 0015](adr/0015-github-unpackaged-setup-and-optional-local-runtime.md) defines the
online/offline Setup, privilege, verification and update rules. The app depends on
both .NET and ASP.NET Core frameworks; end users do not need the SDK.

- Online Setup downloads only missing/incompatible prerequisites: .NET installers
  from Microsoft, Windows App Runtime and VC redist from the same GitHub Release.
  Offline Setup contains all prerequisites; compatible installed versions are reused.
- .NET minimums come from the published `runtimeconfig.json` and the selected ASP.NET
  Core runtime's .NET dependency. The [pinned download](../tools/release/setup/dotnet-runtime.json)
  is a fallback, not a forced servicing upgrade. Binary compatibility and security
  servicing recommendations are separate requirements.
- Windows App Runtime minimums come from signed MSIX identities. Compatible stable
  DDLM packages are reused despite versioned identity names. The [VC manifest](../tools/release/setup/visual-cpp-runtime.json)
  retains a conservative threshold; an earlier compatible toolchain minimum has not
  been established.
- The installer declares OS build 19041, but .NET 10 supports a narrower set of
  Windows versions; see [README system requirements](../README.md#系统要求与安装).

## Dependency boundaries

- Remote APIs use framework HTTP/JSON rather than provider SDKs; credential and
  data boundaries are defined in [PRIVACY.md](../PRIVACY.md).
- Inference runtimes and model files are excluded from core publish. Optional
  executable components require authenticated manifests and verification before activation.
- NuGet vulnerability warnings `NU1901`–`NU1904` are errors in repository and CI builds;
  direct and transitive dependencies are reviewed.
- Test packages (xUnit, Microsoft.NET.Test.Sdk, coverlet), signing secrets and local
  trust material are excluded from app/Setup output.
