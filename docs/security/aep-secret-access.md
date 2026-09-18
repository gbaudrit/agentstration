---
title: AEP Secret access security
---

# AEP Secret access security

## Trust boundary

An AEP extension is an out-of-process workload. The Console trusts only the configured registration endpoint and its pinned extension identity. `StaticBearer` and `IAepAccessTokenProvider` authenticate Console-to-extension protocol calls; they grant no access to a workload Secret. The registration's transport credential is a distinct Secret and never becomes a workload Secret grant.

The extension declares logical requirement IDs, while the consuming Model Profile stores scoped Secret bindings. Immediately before an invocation, the host verifies the declaration, binding, pinned extension identity, consumer scope, and Secret/Vault use policy. The host sends one 256-bit, one-use capability per bound requirement through the authenticated AEP call. The extension uses `AepSecretAccessClient` to redeem a grant through the host callback. The host resolves through `ISecretResolver` only at redemption, so deletion, Vault unavailability, and policy revocation after issuance are enforced.

The callback endpoint accepts a capability as its workload credential and therefore deliberately allows anonymous HTTP callers. Possession is constrained by 256-bit unpredictability, a one-minute expiry, exact extension/requirement/execution matching, atomic single use, call-end revocation, a 4 KiB request limit, and per-address rate limiting. Remote callbacks require HTTPS; loopback HTTP is permitted for a same-host extension. Operators must expose `Agentstration:Aep:SecretAccess:PublicBaseUrl` only through a trusted network path. A stolen capability can be used once before expiry. Once an authorized extension receives the value, the host cannot prevent that extension from copying or transmitting it; extension installation and endpoint identity therefore remain trust decisions.

## Data and diagnostics

Secret values never enter extension manifests, Model Profile definitions, Source or Pack artifacts, or ordinary AEP options. A grant contains no Secret or Vault name. The capability service keeps only a SHA-256 digest of the handle and the scoped binding in bounded memory. The callback emits raw bytes as bounded Base64 inside a transient JSON response with `Cache-Control: no-store`; the extension SDK returns a byte array that the extension must erase after use. The host disposes its owned `SecretValue` buffer. JSON and HTTP transport buffers may temporarily retain copies until reclaimed by the runtime.

Tracing redacts `secretAccess`, `secretCapability`, `secretValueBase64`, authorization headers and other sensitive fields. Invalid JSON bodies are omitted rather than retained verbatim, and diagnostic URLs omit query strings and embedded credentials. AEP Inspector receives already-redacted traces and does not issue Secret grants itself. Errors expose stable codes and a generic message, never the value, token, Secret name, or Vault name. Unknown server error codes are replaced with `secret_access_failed` before they enter extension diagnostics.

Successful issuance and redemption emit structured logs with extension identity, consumer scope and address, requirement ID, execution ID, decision and the log timestamp. Denials log only a stable error code. These are identifiers, not capabilities; logs remain subject to the deployment's normal access controls. No log, audit event, telemetry tag, SignalR message, Work/Flow history, snapshot, export, YAML, Pack, or Source artifact may contain the redeemable handle or resolved value.

## Failure matrix

The capability service rejects wrong extension, consumer, requirement or execution context; unknown or undeclared requirements; missing or malformed bindings; invalid, expired, revoked or replayed capabilities; missing or unavailable Secret/Vault; and sibling or cross-tenant scope access. Concurrent redemption permits one winner. A failed context match consumes the grant so a stolen handle cannot be probed repeatedly. The host callback maps these failures to value-free AEP errors. The tests in `SecretCapabilityTests`, `ResourceScopeApiTests`, `AepSecretAccessApiTests`, `AepConformanceTests`, and `GenAiHttpPayloadCaptureHandlerTests` exercise the relevant unit, Local Vault, HTTP, scope and diagnostic boundaries.

The current protocol covers bound AEP model invocations. Other extension operations must adopt the same issuance and cleanup rules before accepting Secret bindings at runtime. No grant is issued for an extension without the `aep.secret-access` version `1.0` feature.
