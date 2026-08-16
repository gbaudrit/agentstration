# ADR-0044: External identities are explicitly linked to Principals

Status: Accepted — 2026-08-16

## Context

Agentstration already resolves a validated external identity through the exact `(Issuer, Subject)` pair, but the first authentication vertical had no supported way to create or remove those links. Automatically matching an unknown login by email would create an account-takeover boundary, while provider-specific provisioning would violate the provider-neutral Management model.

## Decision

Platform administrators explicitly manage external authentication links for existing human Principals. A link stores the exact, case-sensitive issuer and subject emitted by the configured OIDC provider. Agentstration does not lowercase, alias, or match these values through email, display name, IdP tenant, or Workspace membership.

The Management service enforces the following invariants:

- only an active Platform administrator can list, link, or unlink external identities;
- the target Principal must exist, be active, and be a human Principal when a link is added;
- `(Issuer, Subject)` is unique across the Agentstration instance;
- linking the same pair to the same Principal is idempotent;
- linking a pair already owned by another Principal is rejected;
- an external identity cannot be removed when it is the Principal's final authentication method;
- a local Identity account linked to the same Principal counts as another authentication method.

The lifecycle is serialized inside the authoritative standalone server so concurrent unlink requests cannot both remove the last two authentication methods. SQLite remains the durable authority for pair uniqueness.

The API is:

- `GET /api/identity/principals/{principalId}/external-identities`;
- `POST /api/identity/principals/{principalId}/external-identities`;
- `DELETE /api/identity/principals/{principalId}/external-identities/{externalIdentityId}`.

All three endpoints require the ASP.NET Core `PlatformAdmin` policy. Console **Organization > Members > Member details** delegates to the same service. Successful link and unlink operations append identifier-only security audit events; issuer, subject, claims, and tokens are not copied into the audit log.

## Consequences

- Existing local Principals can acquire one or more provider-neutral external authentication methods.
- Two providers may issue the same subject without collision because issuer remains part of the key.
- Removing a link invalidates its ability to resolve on the next request; no token revocation protocol is invented.
- No database migration is required because `ExternalIdentities` and its unique index already exist.
- This iteration deliberately does not auto-provision unknown OIDC callers, create Principals from email, discover provider users, or implement account linking initiated by an end user.
- Creating external-only Principals requires a separate invitation/provisioning workflow with explicit Workspace assignment and recovery semantics. Until then, administrators link external identities to an existing Principal.
- Multiple independent writers would require a database transaction or coordinator for the final-authentication-method invariant, consistent with the limitation documented for Platform administrator lifecycle mutations.
