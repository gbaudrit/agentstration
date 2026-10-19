# ADR-0133 — Foundry provider Secret binding covers discovery

Status: Accepted — 2026-09-18

## Context

ADR-0121 introduced one-use AEP Secret capabilities for model chat, with bindings on Model Profiles. Foundry also needs the same API key to discover deployments before an administrator can create a profile. A profile-only binding would leave discovery dependent on a process environment key.

## Decision

- A Model Provider may bind an extension-declared logical Secret requirement to a scoped Secret. Foundry declares optional `credential` and advertises `aep.secret-access` version `1.0`. `ApiKeyBinding` requires that grant for both discovery and inference.
- Bound discovery uses an authenticated `POST /aep/model-providers/{providerId}/models/discover` request carrying only one-use grants. The existing unbound `GET .../models` remains available for other extensions and transitional authentication modes.
- The host authorizes a provider binding against the Model Provider consumer and issues a fresh capability per discovery or inference. A Model Profile binding for the same requirement takes precedence for that profile's inference only. Each grant is revoked at operation end.
- The extension redeems the grant once per operation, validates the bounded UTF-8 API key, and uses it only for requests to the configured Foundry endpoint. A multi-page discovery reuses the redeemed key only during that bounded operation. No key is stored in provider/profile resources, manifests, Packs, history or diagnostics.
- AEP transport authentication remains separate. The host callback requires HTTPS for a separate process or loopback HTTP on one host. Compose operators must provide a reachable trusted HTTPS callback URL; Aspire's local processes use the server's loopback endpoint.

## Consequences

Secret rotation, removal and policy changes affect the next operation. Missing or denied bindings fail before Foundry I/O. Existing environment and Entra modes remain for migration and identity-based deployments. The optional discovery POST extends the versioned `aep.secret-access` feature without changing the base AEP protocol version.
