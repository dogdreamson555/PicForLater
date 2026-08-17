# Qwen3-VL clean-machine prerequisites

This document is the release checklist for the two publisher-qualified
PicForLater Qwen3-VL packages. Model inference remains local. The app never
sends an image, OCR text, file name, path, model output, or hardware-derived
content to a download endpoint.

## What a user must install

| Item | CPU Q4F32 | CUDA Q4F16 |
| --- | --- | --- |
| PicForLater x64 Setup and its declared .NET/Windows App SDK runtime prerequisites | Required | Required |
| PicForLater local-inference component | One-click, downloaded when local analysis is enabled | One-click, downloaded when local analysis is enabled |
| Qwen model package | One-click, 3,818,973,177 bytes (3.56 GiB) | One-click, 2,426,419,105 bytes (2.26 GiB) |
| ONNX Runtime / ONNX Runtime GenAI | Included in the local-inference component | Included in the local-inference component |
| Visual C++ developer tools, Python, Hugging Face CLI, `nvcc`, or full CUDA Toolkit | Not required | Not required |
| NVIDIA display driver | Not required | Required; install/update through an official NVIDIA channel |
| CUDA/cuDNN user-mode DLLs | Not required | Reuse a compatible system installation, or one-click app-private installation |

The CPU package therefore has no extra AI-runtime prerequisite. It recommends
16 GiB system RAM; its measured process peak was about 6.43 GiB. Because the
verified download is copied into its final model-package directory before
staging is removed, allow about 7.36 GiB free during the first installation.

The CUDA package is currently an x64 path. It accepts any NVIDIA CUDA device in
the nominal 8 GB class when the driver reports at least 7.5 GiB usable VRAM.
This tolerance deliberately includes common 8 GB RTX 3060, 4060, and 5060
devices that report about 7.9 GiB after reserved regions are excluded. It is
not a GPU-model whitelist. The app records the detected name, memory, compute
capability, and driver-supported CUDA API, then treats the actual model
self-test as authoritative.

## Pinned app-private NVIDIA runtime

ONNX Runtime 1.26.x is built against CUDA 12.8 and cuDNN 9.x. NVIDIA's CUDA
minor-version compatibility permits this runtime on a driver that supports a
newer CUDA API, including a machine whose driver reports CUDA 13.x. PicForLater
still installs CUDA 12 user-mode DLLs because CUDA 13 DLL names are not a
drop-in replacement for a CUDA 12 ONNX Runtime build.

The app-private installer downloads these official NVIDIA Windows x64
redistributable archives:

| Component | Pinned component version | Download bytes | SHA-256 | Extracted allowlist |
| --- | ---: | ---: | --- | --- |
| CUDA Runtime | 12.8.90 | 3,037,735 | `4a39058fd8519444a81cfc7ae055d136f48d1a31ffa41ae255b35b2edd61e13b` | `cudart64_12.dll` |
| cuBLAS | 12.8.5.5 | 563,633,310 | `0a2beedd7c1203cb9de5e5ab11943d27e41ee5d18dc3810b21bcd75be7e57a05` | `cublas64_12.dll`, `cublasLt64_12.dll` |
| cuFFT | 11.3.3.83 | 190,568,498 | `cc6e0ba958cf23387b462017a24464c72bd901549046133f3d1ebcc3d7444c90` | `cufft64_11.dll` |
| NVRTC | 12.8.93 | 305,588,898 | `a63302a077f0248a743a1a7caa7dbd80d0fac56c6cfa9c41fa05fac9b7e5eda5` | `nvrtc64_120_0.dll`, `nvrtc-builtins64_128.dll` |
| cuDNN | 9.25.0.15 for CUDA 12 | 1,904,452,100 | `06e94f70c52d7335b7ed8044eed28ce963b7fd59d8c2c446ffc60e695fccad91` | `cudnn64_9.dll` and the nine cuDNN 9 split runtime DLLs |

Exact runtime download total: **2,967,280,541 bytes (2.76 GiB)**. The UI
declares a conservative private-install maximum of **2,350,000,000 bytes
(2.19 GiB)** before the user confirms. A clean first CUDA setup downloads the
runtime plus model, **5,393,699,646 bytes (5.02 GiB)** in total. Allow at least
about 7 GiB free at peak, or 8 GiB as an operational margin, because model
staging and the final verified copy briefly coexist.

The archives are fetched only after a user confirmation. PicForLater:

1. restricts requests and redirects to
   `https://developer.download.nvidia.com/compute/`;
2. checks the declared byte length and SHA-256 before opening an archive;
3. extracts only the fixed DLL basenames above into a staging directory;
4. writes an installed-file hash manifest and atomically moves the runtime into
   the app's private `%LocalAppData%\PicForLater\model-runtimes` directory;
5. does not run an NVIDIA EXE, require administrator access, copy into
   `System32`, or change machine/user `PATH`;
6. keeps completed verified archives for a canceled or transiently failed
   retry, then removes the download cache after a successful installation.

The confirmation shows NVIDIA's CUDA and cuDNN license links. NVIDIA's display
driver is intentionally not installed by PicForLater because a driver update
requires vendor elevation/reboot handling and is outside the app-private model
runtime boundary.

## One-click behavior

Opening Settings performs a local read-only NVIDIA check; **Check again** runs
it explicitly. Detection uses the NVIDIA driver API (`nvcuda.dll`) rather than
executing `nvidia-smi`, and does not access the network.

When the CUDA model action is chosen:

1. no NVIDIA driver/device, an old driver, or less than 7.5 GiB reported VRAM
   stops before any download and recommends the CPU package;
2. a complete CUDA 12/cuDNN 9 system runtime is reused, so the same files are
   not downloaded again;
3. a missing runtime produces one combined confirmation for the pinned NVIDIA
   runtime and the model; acceptance installs the runtime, rechecks it, then
   downloads, validates, imports, self-tests, and enables the CUDA model;
4. canceling or any failure leaves the current capability slots and existing
   model/data unchanged.

## Release acceptance matrix

Before publishing a release, test from a clean Windows x64 user profile:

- CPU-only PC: CPU one-click succeeds; CUDA action stops before networking.
- NVIDIA 8 GB class (at least RTX 3060, 4060, and 5060-family coverage):
  driver-only machine receives the private runtime and passes the model
  self-test.
- NVIDIA with an existing compatible CUDA 12/cuDNN 9 installation: detection
  reports `System`, downloads no duplicate runtime, and passes the self-test.
- NVIDIA with only a CUDA 13 Toolkit: detection installs private CUDA 12.8 /
  cuDNN 9 files and does not modify the CUDA 13 installation.
- interrupted runtime download: completed archives are reused and a later
  attempt succeeds.
- corrupt archive/hash: installation stops before replacing the active runtime.
- Light, Dark, and High Contrast: detection, warning, success, confirmation,
  progress, cancellation, and failure states remain readable and keyboard
  accessible.
