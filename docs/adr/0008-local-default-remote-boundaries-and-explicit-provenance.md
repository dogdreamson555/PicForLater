# ADR 0008: Local-First Defaults, Remote Data Boundaries, and Explicit Analysis Provenance

- Status: Accepted
- Date: 2026-07-31

> Remote profiles, credentials, and combined-job snapshots are added incrementally by [ADR 0009](0009-remote-profiles-credentials-and-combined-job-snapshots.md); this ADR's privacy boundaries and failure semantics remain in force.

## Context

Import, managed image storage, SQLite, persisted `AnalysisJob` records, stage checkpoints, `AnalysisWorker`, candidate merging, revision/manual-edit protection, reminders, and the recycle bin already form the main pipeline. Third-party APIs may be integrated only as optional Providers that reuse this pipeline; they must not establish a second import, results, reminders, or deletion architecture. `ProviderId` is an opaque identifier for audit and adapter selection; it does not determine output semantics.

## Decision

### 1. Execution Target and Defaults

New installations, upgraded users, and old jobs missing future optional fields all use `Local` as the default execution target. `AnalysisMode.OcrOnly/Balanced/AlwaysEnhance` describes local performance strategy only; it does not express a privacy boundary.

Job snapshots store `AnalysisExecutionBackend { Local, RemoteApi }` and, for remote execution, `RemoteInputMode { LocalOcrText, DirectImage }` as orthogonal fields. New snapshot fields must have local defaults and must not change existing positional parameters; old JSON, database defaults, and old settings all resolve to local. Settings changes affect only new jobs and reanalysis explicitly initiated by the user.

### 2. Remote Payloads

- `RemoteOcrText` first completes local OCR and deterministic entity extraction, then sends only the OCR plain text, language, output-language strategy, and disclosed reference date and time zone needed to generate draft results and reminder candidates. It does not read or send images, thumbnails, paths, original filenames, hashes, internal IDs, EXIF, or library context.
- `RemoteVision` sends only a one-time analysis copy decoded from the immutable original, re-encoded with EXIF/XMP removed, and subject to pixel and byte limits. By default it includes no OCR, path, original filename, hash, internal ID, or library context. The copy is cleaned up when the call ends. Skipped OCR must be recorded as `SkippedByRemoteDirectImage`; it must not be represented as successful empty OCR or produce a bounding box.

Both modes return only structured drafts and candidates in the existing formats; remote category suggestions are empty. The model cannot directly create reminders, schedule notifications, call tools, open URLs, or make secondary network requests.

### 3. Credentials, Consent, and Pre-Send Checks

API keys/tokens are stored only in Windows Credential Locker or equivalent user-level OS secret storage. SQLite, ordinary settings, job snapshots, checkpoints, and logs store only credential references; they do not store secrets, Authorization headers, complete requests/responses, images, or base64 data.

Before remote analysis is enabled for the first time, versioned consent is required. It must identify at least the provider, endpoint host, model, whether text or images are sent, the scope of automatic processing, third-party retention/training statements and their verification date, possible costs, and how to disable the feature. Changes to the input type, provider/endpoint, field scope, or policy statements invalidate prior consent.

Before every send, recheck that the job explicitly selected remote execution, the profile is verified and enabled, capabilities match, credentials exist, consent is still valid, and the job has not been revoked. Network availability or the mere presence of a secret does not authorize sending.

### 4. Failures Do Not Fall Back Across Privacy Boundaries

A local failure must not upload data. A remote failure must not silently switch providers, upgrade OCR text to an image, use a local model, or automatically retry a request whose outcome is uncertain and that may incur a charge. A text-mode failure retains submitted OCR, deterministic candidates, and the extractive draft. An image-mode failure retains the original and existing results, and waits for the user to explicitly retry the API or reanalyze locally. Cancellation can prevent data not yet sent and subsequent commits, but cannot recall data already received by a third party or charges already incurred.

### 5. Main Pipeline and Historical Data

Remote Providers reuse job leases, stage checkpoints, the structured parser/draft, `ReminderCandidateMerger`, revision/manual-field protection, and the atomic completion path. Do not rewrite the `AnalysisWorker` main flow, add a parallel database, or allow provider DTOs/error codes to leak into Core, App, or SQLite. The specific profile and Provider contracts are supplemented by ADRs 0009–0012.

Migration 7 adds explicit stage provenance only to `AnalysisStageResults`; it does not rewrite `AnalysisJobs.ModelProfileSnapshotJson`. Create a verifiable backup before migration; on failure, roll back and preserve the original database. Conservatively backfill old stage rows with `ExecutionLocation=Local`. Mark OCR, deterministic entities, model-free routing, and extractive composition as `OcrFacts`, `DeterministicEntityCandidates`, `RoutingDecision`, and `ExtractiveDraft`, respectively; mark old results with model identity as `ModelGeneratedDraft`.

### 6. Explicit Output Semantics

`AnalysisProvenance` records `ExecutionLocation` (`Local`/`RemoteApi`), `OutputKind` (`OcrFacts`, `DeterministicEntityCandidates`, `RoutingDecision`, `ModelGeneratedDraft`, `ExtractiveDraft`, and `Unspecified` for legacy/unknown data only), and the existing Provider/model/hash/schema fields. Business behavior is determined by these explicit fields; `ProviderId` is used only for audit, display, adapter selection, and capability-profile identification. Do not infer draft source, upload scope, candidate eligibility, or failure fallback from an ID prefix or specific provider.

## Consequences

- Local behavior and the default execution target are unchanged. This ADR itself does not enable networking, add credential storage, or change upload scope.
- Provenance does not rely on provider strings to express execution location or output kind. A new Provider that does not declare `OutputKind` is treated as `Unspecified` and receives no model-suggestion semantics; Providers must explicitly declare output types.
- Migration 7 is a backward-compatible incremental migration. Remote snapshot fields, the credential service, and API profiles are implemented in later vertical slices.
