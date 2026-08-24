# ADR-0070: Personal access tokens are revocable Workspace delegations

Status: Accepted — 2026-08-24

## Context

Interactive users authenticate with a local ASP.NET Core Identity account or an external OIDC provider. Scripts and command-line clients also need a simple credential for the Agentstration API, but the platform does not expose an OAuth authorization server and must not invent a general identity protocol. Reusing a password or application cookie would be unsafe, while an unrestricted token would bypass the contextual Workspace authorization model.

## Decision

Agentstration supports first-party personal access tokens (PATs) as revocable credentials delegated by an active human Principal. PATs are not Principals, local accounts, OAuth access tokens, Platform administrator grants, or a replacement for future workload identities.

A PAT is pinned to one Principal and one Workspace. At creation, its requested permission set must be a subset of the Principal's effective permissions in that Workspace. On every request, authorization computes the intersection of:

1. the Principal's current Workspace membership and role permissions;
2. the PAT's stored permission allow-list;
3. the PAT's active, unexpired and non-revoked state.

Consequently, removing membership, disabling the Principal or Workspace, reducing a role, expiring the token, or revoking it takes effect on the next request. A PAT never receives `PlatformAdmin`, cannot switch Workspace, and cannot create, list, or revoke PATs. Credential administration requires an interactive cookie/OIDC session. A Platform administrator may inspect PAT metadata and revoke one or all PATs belonging to another Principal, but cannot reveal their secrets.

The token is an opaque Bearer value containing a public token identifier and a 256-bit random secret. The complete value is returned exactly once. The Control Plane stores metadata and a SHA-256 digest of the random secret; authentication compares digests in constant time. Expiration is mandatory and capped at 365 days. `LastUsedAt` writes are throttled to avoid a database write on every API request.

The metadata lives in the Management Control Plane SQLite boundary because it links Principal, Workspace and Agentstration permissions. ASP.NET Core Identity continues to own only local account credentials. PAT authentication is an ASP.NET Core authentication scheme selected for Bearer values with the Agentstration PAT prefix. Endpoint policies remain the authorization source of truth.

The initial permission vocabulary deliberately reuses existing permissions:

```text
workspaces/read
resources/read
resources/write
resources/delete
runs/read
runs/execute
```

No separate PAT role hierarchy is introduced.

## Consequences and limits

- Local-first installations can use PATs without an external service or Internet access.
- APIs must declare a contextual permission policy; authentication alone is not sufficient for PAT access.
- Revocation is immediate at request validation time, but cannot cancel an operation already accepted and running.
- PATs are bearer credentials and must be protected like passwords. Only a non-secret prefix is displayed after creation and audit records never contain the token.
- PATs represent delegated authority from a human Principal. Service accounts, client credentials, rotation automation, fine-grained resource selectors, IP constraints and OAuth token exchange remain future workload-identity work.
