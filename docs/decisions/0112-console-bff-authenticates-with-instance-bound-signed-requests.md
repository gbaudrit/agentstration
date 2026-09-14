# ADR-0112 — The Console BFF authenticates with instance-bound signed requests

## Status

Accepted

## Context

ADR-0111 introduced an independent operations Console process. Its private session-resolution and delegation calls need a technical identity that is neither a browser identity nor a Personal Access Token, external Bearer token, or AEP credential. The mechanism must work offline in the standalone development workflow, Aspire, and Compose, permit credential overlap during rotation, reject replay and cross-instance use, and avoid persisting or logging credential material.

## Decision

The operations Console uses the stable workload identity `console-bff` for its private calls to the authoritative server. Each request is authenticated by a dedicated HMAC-SHA256 credential read from a protected file. The canonical request binds the HTTP method, exact path and query, authoritative instance identifier, Unix timestamp, random nonce, and SHA-256 content hash. The signature and safe workload/credential identifiers are transported in BFF-specific headers.

The authoritative server exposes BFF operations only below `/api/internal/bff`. Those operations select a dedicated authentication scheme and authorization policy explicitly; browser cookies, PATs, OIDC Bearer tokens, development principals, and AEP credentials cannot satisfy it. A successful BFF principal contains only workload and credential claims and therefore receives no Platform, Tenant, Workspace, or business-resource permission.

The server accepts a bounded list of configured credentials. Multiple active credential identifiers may overlap during rotation. Setting `Revoked` rejects a credential on the next request; the key file is also read for every request so replacement material is observed without caching. Timestamps have a bounded window and `(credential, nonce)` pairs are accepted once within that window. The target instance identifier prevents a request signed for one authoritative instance from being replayed against another.

Aspire and Compose provision a random development credential into host-managed or named-volume storage and mount/reference it from configuration. Manual standalone deployments use `scripts/security/provision-bff-workload.ps1`. Configuration contains paths and stable identifiers, never key material. Enrollment, overlap/rotation, revocation, successful authentication, and authentication failures are written to the existing append-only security audit using safe credential references. Secrets and credential paths are excluded from responses, audit fields, and logs.

This is a bounded Console-BFF mechanism, not a general workload-identity framework. Future private session and delegated-token endpoints reuse this policy and signed client rather than introducing another credential type.

## Consequences

The independent Console can prove its process identity before later session-resolution and delegated-token increments are added. Operators can rotate credentials by adding a new identifier, updating the Console, and then explicitly revoking the old identifier.

Replay state is local to the authoritative process and bounded by the configured window. This matches the current single authoritative-server topology. A future clustered topology must provide a shared replay store or terminate BFF authentication at one authoritative ingress before scaling this mechanism horizontally.
