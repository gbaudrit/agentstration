# ADR-0079: Management resources use explicit hierarchical scopes

Status: Accepted — 2026-09-07

## Context

Management resources were owned through nullable `TenantId` and `WorkspaceId` fields. That representation duplicated topology, made instance and tenant ownership implicit, and coupled logical identity to a fixed three-case column layout. The product has not shipped this schema, so compatibility migration and dual-write behavior would add cost without preserving user data.

## Decision

Management persists a `ResourceScopes` hierarchy with an internal numeric `Id`, a unique canonical `Ref`, a `Kind`, a `TargetKey`, and an immutable nullable `ParentScopeId`. V1 creates these nodes:

```text
/instance
└── /tenants/{tenantId:D}
    └── /workspaces/{workspaceId:D}
```

Creating a Tenant or Workspace creates its scope in the same database transaction. The instance scope is seeded when a fresh Management store is initialized. The generic hierarchy and parent link allow another scope kind, such as project, to be added without adding another ownership column to every resource.

Every `ControlPlaneResources` row has one required `ScopeId` foreign key. Resource rows no longer contain ownership `TenantId` or `WorkspaceId` columns. The public local resource envelope exposes the immutable canonical `scopeRef`; portable input manifests omit it and the receiving operation assigns the authorized target scope. A resource UID remains its globally unique physical identity and cannot move between scopes.

Exact logical identity is `(scope, namespace, kind, name)`. Exact reads, writes, lists, and deletes are explicit store operations. Visible enumeration walks only the target scope and its ancestors. A workspace therefore sees its workspace, tenant, and instance resources; a tenant sees its tenant and instance resources; instance sees only instance resources. Siblings and descendants are never visible. Homonymous resources remain separate results: visibility does not merge, shadow, or override them. A system lookup that omits scope fails when the logical address is ambiguous.

Request contexts authorize the exact scopes they may access. Tenant and instance writes are never inferred from a workspace request. Resource-kind policy remains separate: support in the generic store does not make every kind valid at every scope.

Bootstrap Profile and Pack definitions default an omitted `targetScope` to `workspace`. Tenant and instance targets must be explicit. No automatic promotion from workspace to a broader scope is allowed.

SQLite databases and the PostgreSQL Management schema are fully reseeded. The PostgreSQL migration history is replaced with a new initial migration. There is no backfill, compatibility table reconstruction, legacy-column support, or dual write. An old SQLite schema fails initialization with a message requiring reset; it is never deleted automatically.

## Consequences

- Ownership and hierarchy have one data-backed representation shared by exact lookup, visibility, and authorization.
- Resource payloads no longer duplicate tenant/workspace ownership.
- Scope parents are immutable and topology creation cannot leave a Tenant or Workspace without its scope.
- SQLite remains the executable local default and PostgreSQL remains behaviorally aligned.
- The pre-release schema change is intentionally breaking for existing local databases and requires reseeding.
- This decision partially supersedes the ownership and identity portions of ADR-0031, ADR-0035, and ADR-0077.
