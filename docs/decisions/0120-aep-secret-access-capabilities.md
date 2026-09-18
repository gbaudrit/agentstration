# ADR-0120 — AEP Secret access uses one-use capabilities

Status: Accepted

## Context

ADR-0040 deferred the protocol for delivering a bound Secret to an out-of-process AEP extension. Extension configuration and Packs must remain portable and must never contain Secret values. AEP transport authentication proves the Console to an extension; it does not authorize an extension to select a Secret.

## Decision

- The extension declares stable logical requirements through `aep.secret-requirements`. A consumer binds each used requirement to an explicitly scoped Secret. The manifest never includes Secret names or values.
- An extension that uses runtime Secret access explicitly advertises `aep.secret-access` version `1.0`. The existing AEP protocol version remains `2026-08-01`. An extension without this feature continues to work when no Secret binding is needed; a bound invocation fails closed if the feature is absent or incompatible.
- Immediately before each model invocation, the host verifies the manifest identity, declared requirements, bindings, consumer and extension scopes, and the Secret and Vault access policy. It issues a random 256-bit, one-use capability for each bound requirement. The host retains only a digest, the binding and the execution context for at most one minute. Each grant contains a logical requirement ID, execution ID, extension ID, capability and callback endpoint. It contains no Secret identity or value.
- The grant travels in the AEP chat request over the existing authenticated extension channel. Possession of that grant is the callback credential; the host matches the extension, requirement and execution against the stored context. This assumes that the registered endpoint and its expected extension identity are trustworthy and that HTTPS protects a remote extension connection. The callback endpoint accepts HTTPS or loopback HTTP only. A stolen grant can be redeemed once before expiry, so transport and log redaction are security requirements.
- The extension calls `POST /api/aep/secrets/redeem` through the AEP SDK when it needs the value. The host invokes `ISecretResolver` only then, rechecking access and availability. The response carries 1–65,536 raw bytes encoded as Base64, with `no-store` headers. Callers must erase returned byte arrays when finished. The host disposes its Secret buffer after response construction; the transient JSON encoding cannot be erased once transmitted.
- A capability is consumed atomically, including on a denied, expired or mismatched attempt. The host revokes unused capabilities when the model call or stream ends. Expiry, cancellation and access revocation also fail closed. The callback is separate from `IAepAccessTokenProvider` and AEP `StaticBearer`, which authenticate host-to-extension transport and are never reused as Secret values.
- Stable, value-free error codes cover unsupported version, invalid/expired capability, context mismatch, denied access, unavailable Secret or Vault, and invalid or oversized value encoding. Capability and value fields must be redacted from traces, telemetry, errors and diagnostics.

## Consequences

An extension can request only grants supplied for a declared and bound requirement. The host needs `Agentstration:Aep:SecretAccess:PublicBaseUrl` when a model profile uses Secret bindings; this must be a HTTPS URL reachable by the extension or a loopback HTTP URL for a same-host extension. No external broker or Vault provider is required for the local default. Long-running calls beyond one minute must use a new invocation rather than retaining a capability.

This decision supersedes ADR-0040 only for its deferred AEP credential-forwarding protocol. Its storage, scope and local Vault decisions remain accepted.
