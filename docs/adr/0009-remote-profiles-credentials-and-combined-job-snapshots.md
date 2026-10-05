# ADR 0009: Remote Profiles, Credentials, and Combined Job Snapshots

- Status: Accepted
- Date: 2026-07-31

## Context

[ADR 0008](0008-local-default-remote-boundaries-and-explicit-provenance.md) established local-by-default behavior, two payload boundaries, versioned consent, and a prohibition on cross-mode fallback. This ADR defines a shared job snapshot for local and remote configurations while preserving compatibility with existing jobs. Remote API configuration does not belong in `ModelPackages`, which has local-file semantics, and API keys must not enter SQLite, ordinary settings, or job snapshots.

## Decision

1. Core adds `AnalysisExecutionBackend { Local = 0, RemoteApi = 1 }` and `RemoteInputMode { LocalOcrText = 1, DirectImage = 2 }`. `Local = 0` makes legacy JSON that lacks this field resolve to local through the CLR default value.
2. Keep the positional constructor of `ModelProfileSnapshot(AnalysisMode, Revision, Slots)` unchanged, adding only init-only `ExecutionBackend` with a default value, nullable `RemoteInputMode`, and nullable `RemoteApiProfileSnapshot`. Remote fields must be empty in local snapshots.
3. `RemoteApiProfileSnapshot` freezes the non-secret configuration at job creation: profile/provider/endpoint, base URI, model, prompt/schema, payload and timeout limits, credential reference, and consent version. It excludes API keys, Authorization, request bodies, and full responses.
4. Migration 8 creates `RemoteApiProfiles` and adds only `ExecutionBackend`, `RemoteInputMode`, and `RemoteApiProfileId` to the single `AnalysisSettings` row, defaulting to `Local/NULL/NULL` respectively. Do not modify released migrations 1–7 or rewrite legacy job snapshot JSON.
5. `AnalysisSettings.ProfileRevision` remains the sole configuration revision. Increment it when the local mode, model slots, execution target, or selected remote profile changes; do not create a separate remote revision.
6. `CombinedAnalysisProfileSnapshotProvider` combines the local capability snapshot with remote execution state and returns a result only when revisions match, with bounded retries on conflict. A remote snapshot can be created only from a profile that is enabled, verified, supports the selected input mode, and has a matching consent version/mode.
7. At this stage, profiles store only HTTPS endpoints, capabilities/limits, policy links and verification time, verification status, versioned disclosures/consent, and credential references. Expanding the data scope, switching provider/endpoint, changing the prompt/schema/policy, or increasing payload scope clears prior consent. If the currently selected profile would become unavailable, the user must explicitly switch back to local before saving; do not switch silently.
8. `IRemoteApiCredentialService` is defined in Core, and the Windows implementation uses the current user's Credential Locker (`PasswordVault`) without depending on package identity. All operations save, read, check, or delete secrets only through stable references; they do not cache or log plaintext. SQLite, `settings.json`, and legacy `ApplicationData.LocalSettings` do not store secrets. An unpackaged process lacks MSIX container isolation. This boundary protects data at rest but cannot defend against a malicious process already running with the same user's permissions.

## Consequences

- New installations, upgraded users, and legacy job JSON all resolve to `Local`. A remote selection affects only jobs created afterward; existing jobs continue to use their own snapshots.
- Adds a separate profile table and a small number of settings columns; does not introduce an SDK, requests, accounts, telemetry, model files, or a resident service.
- Credential Locker secret lifecycles are independent of profile rows. Deleting a profile does not revoke the provider-side key; future UI must explain this separately and coordinate the handling.
