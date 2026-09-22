# ADR-0122 — AEP contributions declare protected Value Requirements

Status: Accepted

## Context

AEP initially exposed one extension-wide `SecretRequirements` list and one chat-only `SecretAccess` request field. That shape could not describe visible installation values, could not isolate requirements belonging to different contributions in one extension, and did not carry connection inputs during model discovery. Agentstration also needs to preserve the governed one-use Secret callback rather than turning every connection value into a Secret or transmitting raw Secret bytes in ordinary AEP requests.

## Decision

- AEP protocol `2026-09-18` replaces `SecretRequirements` with one contribution-scoped `ValueRequirements` declaration. There is no compatibility shim or parallel legacy declaration.
- A requirement identifies its contribution kind and contribution id, logical id, required status, scalar type, optional format and protection: `standard` or `secured`.
- `aep.value-requirements` version `1.0` describes the manifest contract. `aep.bound-values` version `1.0` describes the bounded invocation envelope used by model discovery, chat and streaming.
- A bound value is exactly one of an inline JSON scalar or an existing one-use Secret access grant. A `secured` requirement rejects inline values. A `standard` requirement may receive either representation.
- The host validates contribution ownership, duplicates, unknown and missing requirements, variants, types, protection and size before invoking the extension. Values and grants are redacted as one unit from HTTP traces and Inspector diagnostics.
- The extension SDK resolves both representations by logical requirement id. Inline values are returned locally. Secret grants are redeemed through the existing `aep.secret-access` callback, and the returned byte buffer is erased when the resolved value is disposed.
- Extension transport authentication remains independent from workload values. A bound-value request never contains an Agentstration resource name or scope.

## Consequences

One extension process can host contributions with independent logical connection contracts. Agentstration can bind the same requirement differently for each consuming resource without exposing installation resource identities to the extension. Existing AEP extensions and clients must update atomically to protocol `2026-09-18`. The one-use Secret capability lifecycle and callback remain unchanged.

This decision supersedes the requirement declaration and chat request shape in ADR-0121. ADR-0121 remains authoritative for Secret capability issuance, redemption, expiry, revocation and value bounds.
