# ADR-0074: Initial topology is declarative and Platform administration is global

Status: Accepted — 2026-08-29

## Context

The first declarative bootstrap handler coupled creation of a Platform administrator to one configured Tenant, one Workspace, memberships, and an Owner role. That made a global instance role appear subordinate to one topology and left Tenant and Workspace defaults hidden in host configuration. It also prevented a bootstrap bundle from expressing multiple independent Tenants and Workspaces.

## Decision

Initial topology is expressed with four independent `agentstration.io/v1` resource kinds: `PlatformAdministrator`, `Tenant`, `Workspace`, and `PrincipalDefaultContext`. Lexically ordered manifests express their dependencies explicitly. `Workspace` references its Tenant by name. `PrincipalDefaultContext` references a local account, Tenant, and Workspace by name.

`PlatformAdministrator` creates only the local account, Management Principal, local identity link, and instance-wide administrator grant. It creates no Tenant, Workspace, membership, or role assignment. An active Platform administrator is authorized across every active Tenant and Workspace, including future ones, and can select any of them as request context.

`PrincipalDefaultContext` persists only an initial navigation preference. It does not grant access. For ordinary Principals, the authorization service still requires Workspace membership and scoped role assignments. Existing declared resources are skipped; a conflicting default context is reported without reconciliation.

The one-time interactive local bootstrap asks for Tenant and Workspace values and invokes the same decoupled account and topology provisioners. Base application settings contain no implicit initial topology. Unattended non-Development startup requires an explicit manifest path and externally supplied referenced secrets. The versioned Development bundle deliberately uses `admin / admin`, Tenant `dev`, and Workspace `default`.

This decision supersedes the topology coupling described for `PlatformAdministrator` in ADR-0073. ADR-0073's loader, dispatch, ordering, initial-state, idempotency, and secret-handling decisions remain accepted.

## Consequences

- Bootstrap can declare multiple Tenants and Workspaces without treating one as structurally special.
- Platform administration remains visibly independent from Workspace roles and membership lists.
- A global administrator always has a valid fallback context when at least one active Workspace exists; the explicit default controls the preferred initial selection.
- Deployments must declare or interactively choose their initial topology instead of inheriting names from `appsettings.json`.
- No compatibility alias for the former `personal` Workspace default is introduced because it was not released.
