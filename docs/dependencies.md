# Dependency register

This document lists the primary direct and release-relevant transitive dependencies.
Complete attribution and redistribution notices are in [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).

| Dependency | Version | Purpose | License / terms | Release scope |
| --- | ---: | --- | --- | --- |
| Microsoft.WindowsAppSDK | 2.3.1 | Unpackaged WinUI 3 and Windows App SDK integration | Microsoft Software License Terms supplied with the package | Core application; architecture runtime is installed by Setup |
| Microsoft.Web.WebView2 | Transitive | Windows App SDK transitive integration; PicForLater does not directly host WebView2 | Microsoft WebView2 SDK redistribution terms | Transitive |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.8249 | Repeatable Windows SDK build tooling | Microsoft Windows SDK terms | Build time |
| Microsoft.Windows.SDK.BuildTools.WinApp | 0.4.0 | Windows application build tooling | Microsoft package terms | Build time |
| CommunityToolkit.Mvvm | 8.4.2 | MVVM observable and command infrastructure | MIT | Core application |
| CommunityToolkit.WinUI.Notifications | 7.1.2 | Unpackaged desktop notification scheduling and activation | MIT | Core application |
| System.Drawing.Common | 10.0.11 | Security override for a vulnerable transitive version | MIT | Core application |
| Microsoft.Recognizers.Text.DateTime | 1.8.13 | Local natural-language date/time candidates | MIT | Core application; no model or network runtime |
| NuGet.CommandLine | 7.6.0 (`PrivateAssets=all`) | Security override for an obsolete build dependency | Apache-2.0 | Build only; command-line tools are not published |
| Microsoft.Data.Sqlite | 10.0.10 | Local SQLite access and migrations | MIT | Core application |
| SQLitePCLRaw.lib.e_sqlite3 | 3.53.3 | Patched native SQLite library | Apache-2.0 and SQLite public-domain components | Core application |
| Microsoft.ML.OnnxRuntimeGenAI.Cuda | 0.14.1 | Optional x64 Qwen/PP-OCR worker | MIT | Optional local-analysis component only |
| Microsoft.ML.OnnxRuntime.Managed and Microsoft.ML.OnnxRuntime.Gpu.Windows | 1.26.0 | Optional x64 CUDA/CPU inference runtime | MIT | Optional local-analysis component only |
| Microsoft.ML.OnnxRuntimeGenAI.DirectML | 0.14.1 | Optional ARM64 DirectML/CPU worker | MIT | Optional local-analysis component only |

## Dependency boundaries

- Remote API support uses framework HTTP and JSON APIs rather than provider SDKs.
- API credentials are stored through Windows user credential storage and are never part
  of dependency manifests, logs, or published artifacts.
- Local inference runtimes and model files are excluded from the core application publish.
- Optional component manifests are authenticated and their declared sizes, hashes, paths,
  and file sets are checked before activation.
- Repository and CI builds treat NuGet vulnerability warnings `NU1901` through `NU1904`
  as errors and review direct and transitive dependencies.
- Signing secrets and local trust material are not repository or runtime assets.

Test-only packages such as xUnit, Microsoft.NET.Test.Sdk, and coverlet remain confined to
test projects and are not included in the application or Setup output.
