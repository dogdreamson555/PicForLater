# ADR 0011: RemoteVision Sanitized Image Payload and Explicitly Skipped Stages

- Status: Accepted
- Date: 2026-07-31

## Context

[ADR 0008](0008-local-default-remote-boundaries-and-explicit-provenance.md)–[ADR 0010](0010-remote-ocr-text-provider-and-payload-boundary.md) defined the remote boundaries, profile/credential/consent snapshots, and text mode. This stage implements `RemoteApi + DirectImage`, skipping unnecessary local OCR, deterministic OCR entity extraction, and Qwen while reusing the existing job lease, checkpoint, parser, candidate confirmation, revision guard, and atomic completion path.

An empty string cannot distinguish genuinely empty OCR from an intentional skip, and could cause remote text to be mistaken for local facts or evidence of where text appears in the image. The skipped state must therefore be stored explicitly.

## Decision

1. Add a defaulted `AnalysisStageOutcome` at the end of `AnalysisProvenance`, currently `Completed` or `SkippedByRemoteDirectImage`. Existing constructors and JSON that lacks this field still resolve to `Completed`.
2. Schema 10 adds only a non-null `StageOutcome` column, defaulting to 0, to `AnalysisStageResults`. Existing rows remain `Completed`; do not rebuild the table or delete OCR history. For new `RemoteVision` jobs, OCR and deterministic entity checkpoints record `ExecutionLocation=RemoteApi`, `RemoteInputMode=DirectImage`, `SkippedByRemoteDirectImage`, an empty facts payload, and an `analysis.skipped-by-remote-direct-image` warning.
3. The Worker selects a route only from the snapshot's `ExecutionBackend` and `RemoteInputMode`. `DirectImage` does not call `IOcrProvider`, `IEntityExtractor`, `ConditionalAnalysisRouter`, or the local `IVisionCaptionProvider`; it calls the selected remote image Provider directly. Never infer behavior from a Provider ID or provider name.
4. `WindowsImageContentProcessor` decodes the immutable original through the narrow `IRemoteVisionImagePreprocessor` interface, respects orientation, converts to sRGB, and re-encodes PNG using only pixels and fixed 96 DPI. Preserve resolution where possible, shrinking proportionally only above 16 million pixels; if the encoded result exceeds the limit, continue bounded shrinking based on the actual PNG size. The copy does not retain EXIF/XMP, filename, or path, is held briefly in memory only, and is subject to both the profile's `MaxImageBytes` and the 10 MiB Base64 data URI limit. Clean it up as soon as the Provider call ends.
5. Before reading any image, the Provider uses `RemoteApiRequestAuthorizer` to recheck that the current profile is enabled and verified, supports the required capabilities, has valid consent, and has a payload scope matching the job snapshot; it also checks credentials. Before sending, transport verifies again to cover revocation races during sanitization. The request must also include explicit skipped-OCR provenance. Send only the sanitized copy, disclosed reference time and time zone, output-language policy, and fixed prompt/schema; do not send OCR, bboxes, categories, original filename, path, hash, internal ID, EXIF, or any other library context.
6. Both remote adapters share the controlled HTTP transport and strict JSON contract defined in ADR 0010: no tool calls, redirects/cookies disabled, bounded responses, temporary credential reads, and the existing retry/error classification. Provider DTOs do not enter Core, Worker checkpoints, or SQLite.
7. Convert responses to the existing `VisionStructuredResult` and `ExtractiveContentDraft`, with no remote categories. Image-model entities are marked `Source=Model`, `BoundingBox=null`, and `RemoteVisionNoLocalOcrEvidence`. The parser does not use intentionally skipped empty OCR to support numeric facts, and candidate merging does not upgrade their evidence.
8. Successful results continue through `ReminderCandidateMerger` and the original completion transaction. They produce only pending candidates, never create reminders or notifications directly; the revision-conditional update in `CompleteAsync` protects user edits made during analysis.
9. If an image call fails, retain the original image and skipped checkpoint, and leave the job at Vision/`NeedsAttention`. Do not generate an empty extractive draft, call local OCR/Qwen, or switch providers or text modes. A later action can only explicitly retry the API or create a new local reanalysis.

## Consequences

- Execution and defaults for `Local` and `RemoteOcrText` are unchanged; upgraded users and old stage rows remain `Local`/`Completed`.
- RemoteVision reuses the existing Provider seam and adds no import, image, results, candidate, reminder, or deletion database. The schema is upgraded incrementally from 9 to 10. The backup is validated before the upgrade, and the original database is preserved on failure.
- Image sanitization performs decoding/encoding up to 16 million pixels and a small number of bounded re-encodes. The copy exists only in memory; original image bytes, base64, and full requests are not written to persistent storage or logs.
