# Performance and size notes

This document summarizes public release behavior. Artifact sizes vary by release;
the matching GitHub Release page is authoritative for its files and sizes.

## Distribution layout

- Releases provide architecture-specific online and offline `Setup.exe` installers.
- The unpackaged core app uses framework-dependent .NET and Windows App SDK runtimes.
  Online Setup installs only missing/incompatible prerequisites; .NET runtimes come from
  Microsoft. Offline Setup includes the complete prerequisites for installation without
  network access. See [ADR 0015](adr/0015-github-unpackaged-setup-and-optional-local-runtime.md)
  for the release policy.
- Core Setup excludes Qwen and PP-OCR model files and ONNX Runtime, CUDA, and DirectML
  inference payloads. Optional local-analysis components and models are installed after
  explicit user action outside the application installation directory.

## Runtime behavior

- Tray icon creation keeps Windows efficiency mode and process QoS unchanged by
  disabling the library's automatic efficiency-mode adjustment.
- Library queries, thumbnails, and background work are bounded so the UI does not load
  the complete library into memory.
- Local model inference runs outside the main application process. The worker exits after
  a bounded idle period so model and GPU resources can be released.
- Remote-analysis latency, token usage, and cost depend on the selected provider and model.
- Local-analysis speed and memory use depend on the selected model, execution provider,
  available RAM/VRAM, driver, and image contents.

No hardware-specific performance claim is made. Release verification covers build, tests,
publish layout, installer construction, and required resource checks; device-specific
measurements should identify their hardware and procedure.
