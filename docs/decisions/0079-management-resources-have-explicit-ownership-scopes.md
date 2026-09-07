# ADR-0079: Management resources have explicit ownership scopes

Status: Accepted — 2026-09-07

## Context

Management resource UIDs are globally unique, but logical identity was `(workspace, namespace, kind, name)`. Tenant and instance ownership were represented only by empty `WorkspaceId`/`TenantId` combinations. This made tenant-level uniqueness unsafe and made a system lookup by kind/name ambiguous when homonymous resources existed at several levels.

Sources require instance, tenant, or workspace ownership, and the same requirement can apply to later Management resource kinds. A Source-specific store would repeat a cross-cutting identity and authorization problem.

## Decision

Every Management resource row has one explicit, immutable ownership scope:

- `instance`, with no Tenant or Workspace identifier;
- `tenant:{tenantUid}`, with exactly one Tenant identifier;
- `workspace:{workspaceUid}`, with both its Tenant and Workspace identifiers.

`ResourceScope` validates those combinations. Its normalized `ScopeKey` is persisted with `ScopeType`, `TenantId`, and `WorkspaceId`. The UID-derived storage key remains the globally unique physical primary key and durable provenance identity. An existing UID cannot move to another scope.

The exact logical identity is `(scope, namespace, kind, name)`. SQLite and PostgreSQL enforce it with a unique index on `(ScopeKey, Namespace, Kind, Name)`. Homonymous resources at different scopes remain independent and retain different UIDs.

Store contracts distinguish operations intentionally:

- `GetExact`, `ListExact`, `PutExact`, and `DeleteExact` operate on one explicit ownership scope;
- `GetByUid` resolves the globally unique physical identity subject to scope access;
- `ListVisible` returns all resources visible from a target scope;
- existing unqualified operations retain exact workspace behavior for workspace request contexts; an unqualified system name lookup succeeds only when exactly one scope matches and otherwise reports ambiguity, while unrestricted system enumeration remains explicitly global.

Visibility is downward only. An instance resource is visible everywhere; a tenant resource is visible only in that tenant and its workspaces; a workspace resource is visible only in that workspace. Effective visibility returns every matching resource. It never merges, shadows, overrides, or deduplicates homonymous resources.

Workspace contexts can write only their workspace and read that workspace plus its ancestors. Tenant contexts can write only their tenant and read that tenant plus the instance scope. System contexts may operate on any explicit scope. The store enforces these isolation rules; transport/application authorization must still decide whether a caller is allowed to establish a tenant or system context.

This foundation does not declare that every resource kind may be created at every scope. Each vertical continues to define its supported ownership levels and creation authorization.

## Migration

SQLite initialization adds `ScopeType` and `ScopeKey` when absent, classifies existing rows from their current Tenant/Workspace columns, removes the workspace-only unique index, and creates the exact-scope unique index. PostgreSQL applies the equivalent EF migration. Existing workspace rows remain workspace-owned and their payload and UID are unchanged.

## Consequences

- UID remains sufficient for physical references and provenance.
- Logical name resolution cannot accidentally cross ownership scopes.
- Tenant resources with the same name can coexist in different tenants.
- Consumers must choose exact ownership or descendant-visible enumeration explicitly.
- A later resource vertical can adopt multiple scopes without creating a separate persistence model.
- Cross-scope promotion, configuration inheritance, merging, overrides, and payload deduplication remain outside this decision.

This decision partially supersedes the Management logical-identity statements in ADR-0031 and ADR-0035. ADR-0053 continues to govern modules and resource types that are explicitly workspace-owned.
