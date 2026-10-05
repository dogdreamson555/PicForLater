# ADR 0010: RemoteOcrText Provider and Image-Free Payload Boundary

- Status: Accepted
- Date: 2026-07-31

## Context

[ADR 0008](0008-local-default-remote-boundaries-and-explicit-provenance.md) and [ADR 0009](0009-remote-profiles-credentials-and-combined-job-snapshots.md) established the local default, remote payload boundaries, consent, and job snapshots. This stage implements only `RemoteApi + LocalOcrText`, reusing the existing OCR, deterministic entities, asynchronous enhancement Provider, structured parser, candidate merger, checkpoint, revision/manual-edit protection, and atomic completion path.

## Decision

1. The first adapter, `OpenAiCompatibleRemoteOcrTextProvider`, implements the existing `IVisionCaptionProvider` and accepts `VisionAnalysisRequest` to reuse the Worker main flow, but must not call `OpenImageAsync`.
2. The request contains only bounded OCR plain text, a BCP-47 language tag, the `SameAsContent` output-language policy, the disclosed reference UTC time and time zone, and the model, prompt version, schema, and output token limit frozen in the job snapshot. It excludes images, thumbnails, base64, filenames, paths, hashes, internal IDs, bboxes, categories, and any other library content.
3. The first adapter uses the OpenAI-compatible Chat Completions JSON contract; `RemoteApiProfileSnapshot.BaseUri` is the reviewed complete POST endpoint. This stage does not expose custom endpoint UI or claim compatibility with arbitrary APIs. The request contract must not be inferred from `ProviderId` or its prefix.
4. `RemoteOcrText` always runs the full local OCR and deterministic entity stages before calling the remote Provider. Neither local `OcrOnly/Balanced/AlwaysEnhance` modes nor conditional routing can skip the remote call, and the local Qwen Provider is not queried.
5. Responses pass through `QwenStructuredOutputParser` with its strict, versioned JSON schema and evidence validation, then are converted to the existing `VisionStructuredResult`. The adapter supplies empty category context and clears `categoryIds` and `visualFacts`; text mode generates only titles, summaries, and entity/reminder candidates supported by OCR evidence.
6. Remote candidates and deterministic OCR candidates continue through the same `ReminderCandidateMerger` and original completion transaction. Remote output is marked `ModelSuggested`; it produces only pending candidates and does not create reminders or overwrite user fields.
7. When OCR exceeds the profile limit, first select original lines that show signs of dates, times, or locations, then retain leading and trailing segments. The result is strictly bounded by `MaxTextChars` and a `remote.ocr-text-compacted` warning is written; do not truncate silently.
8. On API failure, retain local OCR, deterministic candidates, and the extractive draft. The same atomic completion transaction writes a composition checkpoint, marks the job and image `NeedsAttention`, and stores a redacted error code for an explicit user retry. Failure does not call the local vision model or switch providers or input modes.
9. `AnalysisProvenance` adds nullable `RemoteInputMode`, and migration 9 adds only a nullable column of the same name to `AnalysisStageResults`. Legacy JSON/rows still resolve to `NULL` and local semantics. Remote Vision and TextComposition stages explicitly record `ExecutionLocation=RemoteApi` and `RemoteInputMode=LocalOcrText`.
10. HTTP transport disables redirects and cookies, limits concurrency to one request per host, temporarily reads credentials from Credential Locker, and limits response bodies. Do not retry 401/403; retry a 429 response automatically at most once (two total attempts), reusing a content-free idempotency key and observing bounded `Retry-After`. The current contract does not guarantee provider support for idempotency, so do not blindly resend 5xx errors, timeouts, or network errors with uncertain outcomes; leave retry to an explicit user action.

## Consequences

- `RemoteOcrText` completes analysis through the existing job pipeline. [ADR 0011](0011-remote-vision-sanitized-image-and-skipped-stages.md) incrementally adds `RemoteVision`; settings and protocol extensions are described in [ADR 0012](0012-settings-navigation-and-multi-provider-api-consent.md). Neither changes this document's text-payload boundary.
- Adds no database, import, results, reminders, or deletion pipeline, and no third-party SDK. The schema is upgraded incrementally from 8 to 9; the existing initializer creates a recoverable backup before migration.
