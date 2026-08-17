# Performance and size notes

This document records public release characteristics rather than development-machine
benchmarks. Values may change with dependencies and packaging; the files attached to
each GitHub Release are authoritative for that release.

## Distribution layout

- PicForLater is published as architecture-specific unpackaged `Setup.exe` installers.
- The core application is .NET self-contained and uses the architecture-matched Windows
  App SDK runtime installed by Setup.
- The core application and Setup do not contain Qwen model weights, PP-OCR model files,
  ONNX Runtime, CUDA, or DirectML inference payloads.
- Optional local-analysis components and models are installed only after an explicit user
  action and remain outside the application installation directory.

Representative pre-release measurements placed the core x64 publish ZIP near 50 MiB and
the offline x64/ARM64 Setup files near 140 MiB. These are engineering observations, not
download-size guarantees.

## Runtime behavior

- Library queries, thumbnails, and background work are bounded so the UI does not load
  the complete library into memory.
- Local model inference runs outside the main application process. The worker exits after
  a bounded idle period so model and GPU resources can be released.
- Remote-analysis latency, token usage, and cost depend on the selected provider and model.
- Local-analysis speed and memory use depend heavily on the selected model, execution
  provider, available RAM/VRAM, driver, and image contents.

No hardware-specific performance claim is made. Release verification covers build, tests,
publish layout, installer construction, and required resource checks; device-specific
measurements should always identify their own hardware and procedure.
