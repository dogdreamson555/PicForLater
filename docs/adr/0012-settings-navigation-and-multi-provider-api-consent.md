# ADR 0012: Settings Subpages, Multi-Provider Protocols, and Restricted Custom Endpoints

- Status: Accepted
- Date: 2026-08-01

## Context

[ADR 0008](0008-local-default-remote-boundaries-and-explicit-provenance.md)–[ADR 0011](0011-remote-vision-sanitized-image-and-skipped-stages.md) defined the local default, remote payloads, credential/consent snapshots, and shared analysis pipeline. This stage lets users view and control these boundaries in Settings while supporting multiple providers with explicit contracts and restricted custom endpoints.

## Decision

1. Keep a single top-level “Settings” entry in the top-level `NavigationView`. Within `SettingsPage`, use `NavigationView + Frame` to show “Overview”, “Local Analysis”, and “API Analysis”. When moving local analysis and theme controls, preserve their existing behavior and resource keys, and retain their `AutomationId` values where feasible.
2. Divide the API catalog into official international, official Chinese, aggregator/fast inference, and local/private deployment categories, with a separate custom interface. Maintain each preset's endpoint, model, protocol, authentication, and verification information in [`remote-api-providers.md`](../remote-api-providers.md). Presets expose only verified input capabilities. Until a unified image and structured-output contract is confirmed, expose only OCR text by default; do not enable image upload based on brand name.
3. Migration 11 adds only protocol, authentication, structured-output, endpoint-trust, API-version, and request-constraint fields to `RemoteApiProfiles`. New profile and snapshot fields have default values. Existing databases and JSON retain the established semantics: the OpenAI-compatible protocol, Bearer authentication, JSON Schema output, and fixed HTTPS endpoints. Existing jobs and upgraded users remain `Local`. Later migrations may only append defaulted structured-output, reasoning-level, and wire-format fields; do not rewrite released migrations.
4. The transport assembles requests only according to the explicit `RemoteApiProtocol`, `RemoteApiAuthenticationKind`, `RemoteStructuredOutputMode`, `RemoteEndpointTrustMode`, and request policy. The Worker, pages, and transport must not dispatch by provider ID. Protocol, authentication, and output-format differences must be declared by the profile contract.

   The first Anthropic profile uses Messages, `x-api-key`, a version header, native base64 image blocks, and `output_config.format`. The other initial presets declare an OpenAI-compatible contract. Later presets declare their protocol and authentication through their explicit profiles.
5. OpenRouter profiles explicitly set `allow_fallbacks=false` and `require_parameters=true`; Perplexity Sonar explicitly sets `disable_search=true` to prevent routing to another upstream or implicit external search. These are snapshotted request policies, not brand-string branches.
6. Custom interfaces support only the two protocols, three authentication methods, and three structured-output modes implemented by the product; do not advertise compatibility with arbitrary APIs. Public endpoints must use HTTPS with no userinfo/query/fragment. Resolve DNS again on each connection and reject loopback, RFC1918, CGNAT, link-local, ULA, multicast, and other non-public addresses. Loopback mode accepts only `localhost`, `127.0.0.1`, and `::1` over HTTP/HTTPS; do not allow arbitrary LAN targets. Always disable redirects and cookies, and add credentials only to the final verified endpoint to prevent SSRF, redirect, and authentication-host drift.
7. API keys pass only through `PasswordBox` to the credential service and are cleared immediately; SQLite, profiles, job snapshots, checkpoints, logs, and errors store only credential references. Unauthenticated loopback profiles do not read or send credentials. When replacing/deleting credentials or changing the endpoint, model, protocol, authentication, schema, or network boundary, switch to `Local` first and invalidate verification and consent.
8. Connection tests send only fixed synthetic text or a built-in sample image; they do not read user images, OCR, filenames, paths, hashes, IDs, EXIF, or library content. Tests use the current model, protocol, input mode, and output contract, and warn that charges may apply.
9. When enabling remote use for the first time, switching providers or modes, or changing the consent scope, require the profile to be verified, required credentials to be present, the current mode's synthetic test to succeed, and the user to give explicit consent in a `ContentDialog`, in that order. Only then save versioned consent and select remote. Settings affect new jobs only; failures do not automatically switch provider, mode, or local/remote execution.
10. Both remote modes reuse the existing Worker, checkpoints, structured parser, candidate merger, reminder confirmation, revision guard, and atomic completion path. The API subpage configures profiles only; it does not create HTTP DTOs or establish a second analysis, reminder, or results database.
11. Model reasoning level, output tokens, and timeout go in a collapsed advanced section; ordinary presets still require only a key and model. Advanced values generate requests according to explicit capabilities and wire format, and changes invalidate verification and consent.

## Provider Verification and Mutable Capabilities

Provider endpoints, authentication, protocols, pricing, policies, and model capabilities can change. Preset models are editable, and before enabling one, users must use the official links and connection test provided by Settings. Changes to policies or fixed fields invalidate existing consent. See [`remote-api-providers.md`](../remote-api-providers.md) for detailed verification records.

## Consequences

- New installations and upgraded users still default to local; creating a profile does not select it for remote execution or trigger a remote request.
- The original image, local OCR/Qwen, import, search, categorization, reminders, recycle bin, and main completion transaction retain their existing pipelines.
- The compatibility scope for custom services is explicit and limited; private-network endpoints other than loopback are not supported for now. Future support for enterprise intranets, custom CAs, proxies, other protocols, or special authentication requires separate threat modeling and an ADR.
