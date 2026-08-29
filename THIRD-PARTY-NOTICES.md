# Third-party notices

PicForLater uses the following third-party packages. This file is an inventory; fixed upstream license and notice texts carried by distribution builds are under `licenses/` and mapped in `licenses/README.md`. `Build-Setup.ps1` fails if the required core texts are absent from publish output.

## PicForLater application icon

- Files: `src/PicForLater.App/Assets/PicForLater.svg` and the PNG/ICO derivatives generated from it.
- Purpose: application, window, shortcut, and setup branding.
- Basis: a PicForLater composition using the visual grammar of Microsoft Fluent UI System Icons (`Image` regular and `Bookmark` filled).
- License: MIT, Copyright (c) 2020 Microsoft Corporation; the full notice is distributed at `licenses/fluent-ui-system-icons/LICENSE.txt`.
- Source: https://github.com/microsoft/fluentui-system-icons

## Optional third-party API services

The remote-analysis presets do not add or redistribute OpenAI, Anthropic,
Google, xAI, Perplexity, DeepSeek, Moonshot, Tencent, Volcano Engine, Alibaba,
Zhipu, Baidu, MiniMax, SiliconFlow, OpenRouter, Groq, Together, Ollama, or vLLM
SDK code. They use framework HTTP/JSON APIs against a user-selected service and
the user's own credential. Service privacy, terms, retention/training controls,
model licenses, and charges remain governed by the selected provider and plan;
the reviewed links and qualification level are inventoried in
`docs/remote-api-providers.md`. This section is not a claim that any service is
zero-retention, training-free, continuously available, or compatible with every
model ID.

## Microsoft.WindowsAppSDK 2.3.1

- Purpose: stable WinUI 3 plus the packaged Windows App SDK API/runtime surface.
- License: Microsoft Software License Terms supplied in the package `license.txt`, including the stated Distributable Code requirements and restrictions.
- Notice: preserve and review the package `NOTICE.txt` when assembling distribution materials.
- Source: https://github.com/microsoft/WindowsAppSDK and https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.3.1

The direct dependency is the stable umbrella package, not individually pinned Windows App SDK components. This choice provides the formal Windows App SDK production/distribution terms; the separately evaluated `Microsoft.WindowsAppSDK.WinUI 2.2.1` package was not adopted because its bundled license identified it as Engineering Preview and restricted live-environment use without another agreement.

The umbrella package declares these top-level transitive components: `Microsoft.WindowsAppSDK.Base 2.0.4`, `Foundation 2.3.5`, `InteractiveExperiences 2.1.3`, `WinUI 2.3.0`, `DWrite 2.1.0`, `Widgets 2.0.5`, `AI 2.3.4`, `ML 2.1.74`, and exact `Runtime 2.3.1`. They are implementation dependencies selected by the umbrella package, not separate PicForLater capability commitments. In particular, phase 1 does not invoke the transitive AI/ML APIs or add model downloads or network behavior.

## Microsoft Visual C++ Redistributable 14.51.36247.0

- Purpose: native runtime required by the optional local ONNX Runtime component.
- Distribution: each architecture-specific Setup includes the matching Microsoft-signed redistributable and installs it with Microsoft's supported installer before copying PicForLater.
- License and deployment terms: https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist

## Microsoft.Web.WebView2 1.0.3719.77 (transitive)

- Purpose: transitive SDK/runtime integration through the umbrella package's WinUI component; PicForLater does not directly host a WebView.
- License: Microsoft WebView2 SDK redistribution terms supplied in `LICENSE.txt`.
- Source: https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.3719.77

## CommunityToolkit.Mvvm 8.4.2

- Purpose: MVVM observable properties and commands.
- License: MIT.
- Source: https://github.com/CommunityToolkit/dotnet

## CommunityToolkit.WinUI.Notifications 7.1.2

- Purpose: notification scheduling and activation for the unpackaged desktop app.
- License: MIT.
- Source: https://github.com/CommunityToolkit/WindowsCommunityToolkit

## LocalSendDotNet.Core 0.2.0-preview.5

- Purpose: independent, UI-free implementation of LocalSend v2.2-compatible LAN
  discovery, TLS identity, pairing and receive-node behavior.
- License: Apache License 2.0.
- Source: https://github.com/kusutori/Tonarink at package commit
  `ec45f9e589f016c077788306cccc19101c2beba7`.
- Distribution texts: `licenses/localsenddotnet-core/LICENSE` and
  `licenses/localsenddotnet-core/NOTICE`.

LocalSendDotNet.Core and PicForLater are independent compatibility implementations.
LocalSend is a separate project created by Tien Do Nam and contributors. PicForLater is
not affiliated with or endorsed by the official LocalSend project; references to
LocalSend identify protocol compatibility and interoperability only.

## System.Drawing.Common 10.0.11

- Purpose: security override for an older transitive version selected by the
  notification toolkit; also used by deterministic test-fixture generation.
- License: MIT.
- Source: https://github.com/dotnet/runtime

## Microsoft Recognizers Text 1.8.13

- Packages: `Microsoft.Recognizers.Text.DateTime` and its Recognizers Text
  runtime dependencies (`Definitions`, `Text`, `TimexExpression`, `Number`, and
  `NumberWithUnit`).
- Purpose: local, auditable recognition and resolution of natural-language
  dates and times in Chinese, English, Spanish, French, Portuguese, German,
  Italian, and Turkish.
- License: MIT.
- Source: https://github.com/microsoft/Recognizers-Text and
  https://www.nuget.org/packages/Microsoft.Recognizers.Text.DateTime/1.8.13

`NuGet.CommandLine 7.6.0` is a private build-time dependency override. It
replaces the obsolete vulnerable version declared transitively by the
Recognizers Text package metadata. Its license is Apache-2.0, and neither
`nuget.exe` nor `vswhere.exe` is included in the application output.

## Microsoft.Data.Sqlite 10.0.10

- Purpose: managed SQLite ADO.NET provider.
- License: MIT.
- Source: https://github.com/dotnet/efcore

## SQLitePCLRaw and bundled SQLite

- Packages: `SQLitePCLRaw.core`, `SQLitePCLRaw.provider.e_sqlite3`, `SQLitePCLRaw.bundle_e_sqlite3`, and the direct `SQLitePCLRaw.lib.e_sqlite3 3.53.3` security override.
- Purpose: native SQLite loading and the `e_sqlite3` Windows binary.
- SQLitePCLRaw license: Apache-2.0.
- SQLitePCLRaw copyright: Copyright 2014-2024 SourceGear, LLC.
- SQLite license: public domain.
- Source: https://github.com/ericsink/SQLitePCL.raw and https://www.sqlite.org/copyright.html

## Microsoft ONNX Runtime GenAI 0.14.1

- Packages/assets: x64 uses `Microsoft.ML.OnnxRuntimeGenAI.Cuda` with
  direct `Microsoft.ML.OnnxRuntime.Managed 1.26.0` and
  `Microsoft.ML.OnnxRuntime.Gpu.Windows 1.26.0` compatibility overrides;
  ARM64 uses
  `Microsoft.ML.OnnxRuntimeGenAI.DirectML` with
  `Microsoft.ML.OnnxRuntime.DirectML 1.23.0`. Both use the matching
  `Microsoft.ML.OnnxRuntimeGenAI.Managed 0.14.1` projection.
- Purpose: out-of-process, local-only structured multimodal generation for
  validated Qwen3-VL packages, plus the architecture-appropriate CUDA/CPU or
  DirectML/CPU execution providers reused by PP-OCR.
- License: MIT.
- Source: https://github.com/microsoft/onnxruntime-genai and
  https://www.nuget.org/packages/Microsoft.ML.OnnxRuntimeGenAI.Cuda/0.14.1 and
  https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime.Gpu.Windows/1.26.0
  and
  https://www.nuget.org/packages/Microsoft.ML.OnnxRuntimeGenAI.DirectML/0.14.1

PicForLater redistributes exactly one matching native ONNX Runtime set per
architecture. The unused `Microsoft.Windows.AI.MachineLearning` build/runtime
payload is excluded to avoid packaging incompatible ORT binaries. Model weights
and tokenizer files are separately installed user data and are not covered by
this runtime notice; each model manifest carries its own license and exact
source.

## NVIDIA CUDA 12.8 and cuDNN 9 on-demand runtime files

- Purpose: optional x64 CUDA acceleration for PP-OCR and the qualified
  Qwen3-VL CUDA package.
- Components: CUDA Runtime 12.8.90, cuBLAS 12.8.5.5, cuFFT 11.3.3.83,
  NVRTC 12.8.93, and cuDNN 9.25.0.15 for CUDA 12.
- Licenses: NVIDIA CUDA Toolkit End User License Agreement and NVIDIA cuDNN
  Software License Agreement.
- License sources: https://docs.nvidia.com/cuda/eula/index.html and
  https://docs.nvidia.com/deeplearning/cudnn/latest/reference/eula.html
- Artifact source: https://developer.download.nvidia.com/compute/cuda/redist/
  and https://developer.download.nvidia.com/compute/cudnn/redist/
- Distribution: these files are not included in the core App or Setup. After explicit
  size/source/license confirmation, PicForLater downloads fixed official
  NVIDIA redistributable archives, verifies exact byte lengths and SHA-256
  values, and installs only allowlisted DLLs in app-private storage. It does
  not run an NVIDIA installer or change system `PATH`.

Exact archive versions, sizes, hashes, extracted DLL names, and the
clean-machine acceptance matrix are recorded in
`docs/qwen3-vl-runtime-prerequisites.md`. Release review must recheck NVIDIA's
then-current redistribution terms before changing any pinned component.

## PP-OCRv6-small on-demand model files

- Purpose: optional enhanced local OCR detection and multilingual recognition.
- License: Apache-2.0 as declared by the PaddlePaddle model repositories.
- Sources: https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx and
  https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx
- Distribution: downloaded only after user confirmation and stored outside the
  core App and Setup; exact revisions, byte lengths, and SHA-256 values are pinned in the
  application catalog.

## Qwen3-VL-2B-Instruct on-demand model files

- Purpose: optional experimental local visual description and structured
  title/summary candidates.
- License: Apache-2.0 as declared by the Qwen base model and conversion source.
- Base model: https://huggingface.co/Qwen/Qwen3-VL-2B-Instruct
- Published ONNX packages: https://huggingface.co/DogDreamson/picforlater-qwen3-vl-2b-onnx/tree/b0ffadcc56e0e736aa1310ff75f7c81147ac50bb
- Conversion reference: https://huggingface.co/onnx-community/Qwen3-4B-VL-ONNX/tree/697b1606a44266869c10f9b5a857ee6f7af17c5a
- Distribution: downloaded only after user confirmation and stored outside the
  core App and Setup. The 3.56 GiB CPU Q4F32 and 2.26 GiB CUDA Q4F16 packages are
  optional testing paths. Their three-language publisher qualification does
  not make either one a completed full-golden-set release default.

## Build-only Microsoft packages

- `Microsoft.Windows.SDK.BuildTools 10.0.26100.8249`
- `Microsoft.Windows.SDK.BuildTools.WinApp 0.4.0`

These packages are used to compile the unpackaged app and related build tooling.
They are not shipped as product features. Their Microsoft package/license terms
still apply to any build assets copied into a distribution.

## Inno Setup

- Purpose: build the traditional per-user `Setup.exe`; the compiler itself is
  not stored in this repository or distributed as a PicForLater component.
- License: Inno Setup License, which permits use and redistribution subject to
  preserving its embedded copyright and origin notices.
- Source: https://github.com/jrsoftware/issrc

## Built-in cat capability-test image

- File: `test pic/cat.jpg` (embedded as
  `PicForLater.Analysis.TestAssets.cat.jpg`).
- Purpose: fixed, non-user image used only when the user explicitly runs an
  `API · Image` connection test.
- Dimensions and size: 640 × 960 JPEG, 61,868 bytes.
- SHA-256: `9afff550a763f949ecc3b39dd5a7d17c9225e40e0405da93330fb0a2487aa641`.
- License: [Unsplash License](https://unsplash.com/license), as supplied and
  approved for redistribution by the repository owner.
- Source: [unsplash.com/photos/white-and-brown-long-fur-cat-ZCHj_2lJP00](https://unsplash.com/photos/white-and-brown-long-fur-cat-ZCHj_2lJP00)

The asset is not a library image and contains no PicForLater user data. The
application does not upload it except during an explicit image-mode connection
test, whose possible third-party processing and billing are disclosed in UI.
