# ADR-0039: Authentication and authorization boundaries

Status: Accepted — 2026-08-16

## Context

Agentstration is local-first but also needs enterprise authentication. The existing Management Plane already owned Tenant, Workspace, memberships, roles, and permissions, while a development handler supplied an unrelated global administrator claim. Authentication sources and Agentstration authorization therefore need an explicit boundary.

The repository currently has three distinct Workspace identities: the canonical Management Workspace (`Guid`), the legacy content Workspace (`WorkspaceId(Guid)`), and the Work-owned Workplace Workspace (`string`). The first security vertical can enforce only operations already scoped by the canonical Management Workspace.

## Decision

Agentstration supports local accounts through ASP.NET Core Identity and external identities through standard ASP.NET Core OpenID Connect/JWT bearer handlers.

```text
ASP.NET Core Identity         External OIDC provider
local account + cookie       OIDC cookie / OAuth bearer
             \                 /
              validated ClaimsPrincipal
                        |
                 Principal resolver
                        |
          stable Agentstration Principal
                        |
     WorkspaceMembership + role assignments
                        |
          ASP.NET Core authorization policies
```

Local credentials are technical authentication data. They are stored in a dedicated SQLite Identity database and handled exclusively by ASP.NET Core Identity. Password hashing, lockout, credential tokens, and future confirmation, recovery, MFA, or passkey features are never implemented by Agentstration code. The local `IdentityUser` is linked to a provider-neutral Principal through `LocalIdentity(AccountId, PrincipalId)`; neither `Principal` nor Management records contain password hashes, salts, recovery codes, MFA secrets, or authentication tokens.

External identities are identified by the exact `(Issuer, Subject)` pair and linked through `ExternalIdentity`. Email and display name are attributes, never identity keys. A Principal may eventually have both local and multiple external authentication methods without changing Workspace authorization.

The Management boundary owns Principal, LocalIdentity links, ExternalIdentity links, the canonical Workspace, WorkspaceMembership, role assignments, and the distinct PlatformAdministrator grant. The ASP.NET Core Identity implementation and credential database live in `Agentstration.Security.AspNetCoreIdentity`; ASP.NET Core policy handlers remain in Web. Domain, Application, Management abstractions, and Management core do not depend on a concrete cloud IAM provider or on Identity credential types.

`PlatformAdmin` is an instance-level authorization and is not implied by `WorkspaceAdmin` or Workspace ownership. Workspace permissions remain contextual: an assignment for Workspace A grants nothing in Workspace B. Endpoint code declares policies, while handlers and neutral authorization services evaluate persisted authorization data. Resource-based handlers additionally compare the loaded resource with the selected Workspace.

The initial administration surface lets Platform administrators list, create, enable, and disable local accounts. A new account receives a new Principal and one initial Workspace membership. Workspace administrators manage memberships with the built-in `Owner`, `Admin`, `Member`, and `Viewer` roles. The final active Owner cannot be demoted or removed. Platform administrator accounts cannot yet be disabled because reassignment of the instance role is intentionally deferred.

A fresh local instance contains no account and no known password. The anonymous bootstrap endpoint is available only in Local or Hybrid mode and only while the Identity store has never been initialized. It creates the first Identity account, Principal, local link, initial Tenant and Workspace, Owner assignment, and PlatformAdministrator grant. ASP.NET Core Identity validates and hashes the supplied password. The bootstrap becomes unavailable after the first account and records completion in the credential database.

Authentication modes are:

- `Local`: Identity application cookie and local accounts;
- `Oidc`: OIDC interactive cookie and JWT bearer API tokens;
- `Hybrid`: local cookie plus explicit OIDC login and bearer tokens;
- `Development`/`Disabled`: explicit, isolated Development or Testing-only modes.

Agentstration does not expose `/authorize`, `/token`, or `/userinfo`, does not introduce OpenIddict Server, and does not issue OAuth tokens for third-party applications. API bearer-token support remains a resource-server concern.

## Consequences

- Local installations operate without Internet, Azure, a SaaS provider, or an external IdP.
- Enterprise providers remain configuration choices and cannot influence the Principal or permission model.
- Credentials and business authorization have separate persistence boundaries (`identity.db` and `control-plane.db`). Bootstrap coordinates the two stores and removes the Identity account if Management provisioning fails.
- Identical external subjects from different issuers remain distinct; email changes do not alter identity.
- Human and workload Principals are distinct kinds. Full workload authentication remains future work.
- The first protected vertical covers Agent Management and Identity/Workspace operations. Flow, Runtime, Work, Workplace, MCP, SignalR, and AEP still require explicit scoping work.
- EF Core `EnsureCreated` creates the first Identity schema because `identity.db` is a new dedicated database. Future schema changes must use migrations rather than relying on `EnsureCreated`.

## Follow-up

Add password change/recovery and optional confirmation flows, Platform administrator reassignment, account deletion/retention policy, external-account provisioning/linking, persistent Data Protection configuration, audit events, and authorization coverage for the remaining planes. Reconcile Work/Workplace Workspace identities before applying canonical Workspace policies there.
