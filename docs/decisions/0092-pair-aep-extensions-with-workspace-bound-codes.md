# ADR-0092: Pair manually hosted AEP extensions with workspace-bound codes

- Status: Accepted
- Date: 2026-09-09

## Context

A manually launched extension cannot safely expose its functional AEP endpoints before Agentstration has a credential. Putting a credential or one-time code in a URL would leak it through browser history, logs, proxies, and referrers, while a first-caller flow would permit takeover.

## Decision

`EnrollmentMode=PairingCode` gives every extension installation a durable random instance ID and an extension-hosted form on `/aep/enrollment/pair`. The extension announces that ID, its declared identity, its public AEP endpoint, and the same-origin form URI to the selected workspace. Normal AEP endpoints require authentication throughout enrollment.

An administrator with resource-write permission explicitly rotates a nine-digit code. Agentstration stores only a salted digest, binding, issuance time, 60-second expiry, and failed-attempt count. Rotation invalidates the previous digest. A valid claim atomically consumes the code, creates a 256-bit extension-specific Bearer in a workspace Local Vault, and returns it once to the extension backend. The extension persists only the client identity and token digest, then proves installation with a separate completion token. Agentstration authenticates and identity-pins a manifest request before marking the enrollment available.

The pairing form receives no Console cookie, PAT, or OIDC token. Codes never appear in a path, query, or fragment. Requests and terminal outcomes are durable control-plane resources, making restarts idempotent and the inbox authoritative.

## Consequences

- Concurrent claims have exactly one successful consumer.
- A paired extension never falls back to unpaired because Agentstration is unavailable or presents a bad token.
- Administrators can reject, cancel, or explicitly rotate pending codes.
- Manual enrollment requires a writable state-file location and an HTTPS authority unless insecure HTTP is explicitly enabled for development.
