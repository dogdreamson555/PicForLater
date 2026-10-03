# Dependency register

This document lists the primary direct and release-relevant transitive dependencies.
Complete attribution and redistribution notices are in [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).

| Dependency | Version | Purpose | License / terms | Release scope |
| --- | ---: | --- | --- | --- |
| Microsoft.WindowsAppSDK | 2.3.1 | Unpackaged WinUI 3 and Windows App SDK integration | Microsoft Software License Terms supplied with the package | Core application; architecture runtime is installed per-user by Setup |
| Microsoft.Web.WebView2 | Transitive | Windows App SDK transitive integration; PicForLater does not directly host WebView2 | Microsoft WebView2 SDK redistribution terms | Transitive |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.8249 | Repeatable Windows SDK build tooling | Microsoft Windows SDK terms | Build time |
| Microsoft.Windows.SDK.BuildTools.WinApp | 0.4.0 | Windows application build tooling | Microsoft package terms | Build time |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM observable and command infrastructure | MIT | Core application |
| CommunityToolkit.WinUI.Notifications | 7.1.2 | Unpackaged desktop notification scheduling and activation | MIT | Core application |
| H.NotifyIcon.WinUI | 2.4.1 | Native system-tray icon and PopupMenu integration | MIT | Core application |
| LocalSendDotNet.Core | 0.2.0-preview.5 | UI-independent LocalSend v2.2-compatible LAN discovery, TLS and receive-node implementation | Apache-2.0 | Core application; independent compatibility implementation, not an official LocalSend component |
| Microsoft.NETCore.App | .NET 10 framework reference | Core runtime for the framework-dependent unpackaged app | MIT and .NET third-party notices | Online Setup installs a compatible .NET Runtime 10 only when the required version is absent; Offline Setup includes it. End users do not need the .NET SDK |
| Microsoft.AspNetCore.App | .NET 10 framework reference | Kestrel HTTPS server and hosting primitives required by the LocalSend receive node | MIT and .NET third-party notices | Online Setup installs a compatible ASP.NET Core Runtime 10 only when the required version is absent; Offline Setup includes it |
| System.Drawing.Common | 10.0.11 | Security override for a vulnerable transitive version | MIT | Core application |
| Microsoft.Recognizers.Text.DateTime | 1.8.13 | Local natural-language date/time candidates | MIT | Core application; no model or network runtime |
| NuGet.CommandLine | 7.6.0 (`PrivateAssets=all`) | Security override for an obsolete build dependency | Apache-2.0 | Build only; command-line tools are not published |
| Microsoft.Data.Sqlite | 10.0.10 | Local SQLite access and migrations | MIT | Core application |
| SQLitePCLRaw.lib.e_sqlite3 | 3.53.3 | Patched native SQLite library | Apache-2.0 and SQLite public-domain components | Core application |
| Microsoft Visual C++ Redistributable | `tools/release/setup/visual-cpp-runtime.json` | Native runtime dependencies | Microsoft Visual C++ runtime terms | Installed machine-wide only when the required version is absent; Online Setup fetches the architecture-specific package from the same GitHub Release, Offline Setup includes it |
| Microsoft.ML.OnnxRuntimeGenAI.Cuda | 0.14.1 | Optional x64 Qwen/PP-OCR worker | MIT | Optional local-analysis component only |
| Microsoft.ML.OnnxRuntime.Managed and Microsoft.ML.OnnxRuntime.Gpu.Windows | 1.26.0 | Optional x64 CUDA/CPU inference runtime | MIT | Optional local-analysis component only |
| Microsoft.ML.OnnxRuntimeGenAI.DirectML | 0.14.1 | Optional ARM64 DirectML/CPU worker | MIT | Optional local-analysis component only |

## Dependency boundaries

- Remote API support uses framework HTTP and JSON APIs rather than provider SDKs.
- API credentials are stored through Windows user credential storage and are never part
  of dependency manifests, logs, or published artifacts.
- Local inference runtimes and model files are excluded from the core application publish.
- LocalSendDotNet.Core contributes a `Microsoft.AspNetCore.App` framework reference. The
  application uses framework-dependent .NET 10 and requires both `Microsoft.NETCore.App`
  and `Microsoft.AspNetCore.App`. Online Setup checks for compatible installed runtimes and
  downloads only frameworks that do not meet the required version from Microsoft's official
  installer URLs. Offline Setup carries both installers. The .NET SDK is a development
  prerequisite only.
- Windows App Runtime 2.3.1 is deployed as the applicable Microsoft-signed MSIX packages for
  the native architecture. Online Setup downloads the architecture-specific ZIP and VC runtime
  from the same GitHub Release; Offline Setup includes them. The WinUI app itself remains
  unpackaged, and Windows App Runtime packages are registered for the current user.
- Compatible .NET servicing versions are reused across app updates. Runtime assets are only
  refreshed when a release changes its minimum dependency version; ordinary application
  updates do not require downloading the full runtime set again.
- The installer continues to declare Windows App SDK's minimum OS build, 19041. The .NET 10
  runtime's own supported Windows versions are narrower; Windows 10 support is limited to the
  LTSC / Enterprise versions listed in [Microsoft's .NET on Windows requirements](https://learn.microsoft.com/dotnet/core/install/windows).
- LocalSend-compatible receive traffic is confined to the local network. A received image
  becomes an ordinary PicForLater library item, so a later user-selected remote analysis
  action may send derived text or a re-encoded image to that configured API under the same
  consent and data-boundary rules as any other imported image.
- Optional component manifests are authenticated and their declared sizes, hashes, paths,
  and file sets are checked before activation.
- Repository and CI builds treat NuGet vulnerability warnings `NU1901` through `NU1904`
  as errors and review direct and transitive dependencies.
- Signing secrets and local trust material are not repository or runtime assets.

Test-only packages such as xUnit, Microsoft.NET.Test.Sdk, and coverlet remain confined to
test projects and are not included in the application or Setup output.
