# Qwen3-VL local package contract

PicForLater does not ship Qwen model weights in Git or in the core Setup. A user may
explicitly import a compatible local package or use an application-provided optional
download after reviewing its size, source, license, and hardware requirements.

## Required package information

An importable package must include a `manifest.json` describing:

- package, provider, architecture, and schema versions;
- every managed relative file path, byte length, and SHA-256 value;
- model source and license information;
- supported execution providers and minimum resource requirements;
- model input signature, output schema, and declared language coverage.

The manifest is package metadata, not permission to execute arbitrary code. Runtime
`trust_remote_code`, scripts, absolute paths, path traversal, reparse points, and
undeclared files are rejected.

## Import and activation

1. The selected package is copied into an application-managed staging directory.
2. Paths, file count, sizes, hashes, architecture, and runtime compatibility are checked.
3. A bounded local inference self-test must succeed.
4. The validated package is moved into versioned application data and activated atomically.

A failed import or self-test leaves the previously selected package unchanged. Package
selection affects only newly queued jobs unless the user explicitly requests reanalysis.

## Privacy and distribution

- Package download and import occur only after explicit user action.
- Model files and caches remain under the user's local application-data directory.
- Local inference does not silently fall back to a cloud provider.
- The core application does not upload local images, OCR text, model output, file paths,
  or hardware-derived content to a package download endpoint.

Runtime and driver requirements are documented in
[`qwen3-vl-runtime-prerequisites.md`](qwen3-vl-runtime-prerequisites.md). Exact package
locations, versions, and integrity values are maintained by the signed application catalog
rather than duplicated in this public overview.
