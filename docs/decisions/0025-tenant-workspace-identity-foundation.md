# ADR-0025: Tenant, workspace, identity, and local authorization foundation

Status: Partially superseded by ADR-0031 and ADR-0033 — 2026-08-12

The tenant, workspace, identity, and authorization decisions remain accepted. The additional resource-scope level and hierarchical resource paths below are historical and are not part of the current model.

## Context

The Management control plane previously keyed documents only by an unscoped resource ID. Standalone startup seeded management resources, but it had no persisted user, tenant membership, workspace ownership, or authorization context. A separate legacy content vertical already uses a `WorkspaceId`; that workspace is not the Management security boundary and cannot safely stand in for a tenant.

## Decision

The Management boundary owns the first identity and resource-management hierarchy:

```text
Tenant -> Workspace -> ResourceGroup -> Management Resource
User -> TenantMembership -> RoleAssignment -> RoleDefinition
```

Identity and RBAC records are normalized SQLite tables in the existing control-plane database. Management documents retain their JSON payload and add indexed `TenantId`, `WorkspaceId`, and `ResourceGroupId` columns. Keeping the scope columns outside the payload permits mandatory server-side filtering and safe backfill of existing documents.

Standalone mode uses the same model as future multi-tenant hosting. An idempotent bootstrap creates the local tenant, default workspace, default resource group, local user, membership, built-in Owner role, and tenant-scope assignment. Generated entity IDs are persisted; only the built-in Owner role uses a stable system ID. The resulting `RequestContext(UserId, TenantId, WorkspaceId)` is exposed through an injectable context abstraction and is never read from static state.

Role assignments address a typed principal (`User`, with `Group` and `ServicePrincipal` reserved) and a canonical tenant or workspace scope. Permission evaluation first requires active tenant membership, then accepts assignments at the current workspace or its parent tenant scope. HTTP management routes enforce read/write permissions and reject a workspace route that differs from the authorized context.

Canonical resource identifiers support the target workspace-prefixed form while parsing the legacy form for compatibility:

```text
/workspaces/{workspaceId}/resourceGroups/{group}/providers/{namespace}/{type}/{name}
```

## Migration

Startup performs an additive, idempotent SQLite schema upgrade because the original control plane used `EnsureCreated` rather than EF migrations. It creates the identity/RBAC tables and indexes, adds the three scope columns when absent, and attaches every unscoped management document to the bootstrapped local tenant, workspace, and resource group. No existing resource payload is rewritten or discarded.

## Consequences

- Local startup remains login-free and cloud-independent.
- Tenant and workspace isolation is enforced in the control-plane store and management HTTP routes.
- Existing legacy management URLs remain available during migration, but resolve only inside the current authorized context.
- The general content, Work, Flow, and Runtime stores remain physically independent. Their existing workspace keys continue to work, but adding an explicit `TenantId` to each independent store is a subsequent migration; this ADR does not collapse those bounded contexts into the Management database.
- The standalone identity remains the fallback context. A workspace selection is held in an HTTP-only same-site cookie, revalidated for active membership and `workspaces/read`, and applied through an `AsyncLocal` scope only for the lifetime of the request or interactive connection.
- The Console provides workspace switching, workspace creation, organization/member listings, and inherited-access inspection. Role-assignment mutation, membership invitations, and OIDC identity resolution remain later increments.
