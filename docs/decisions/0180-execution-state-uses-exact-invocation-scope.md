# ADR-0180: Execution state uses the exact invocation scope

Status: Accepted — 2026-10-15

## Context

Reusable Agent and Flow definitions can be owned by the Instance, a Tenant, or a Workspace. The
execution plane still identifies runs, sessions, assignments, input requests, and Work records by a
mandatory Workspace, however. Invoking an Instance- or Tenant-owned definition at its own scope
therefore requires a synthetic Workspace and stores operational state under the wrong owner.

Definition ownership, invocation ownership, and principal ownership are separate concerns. A
Workspace may invoke an authorized Instance definition while retaining its execution state locally;
an Instance invocation of the same definition must instead retain its state at the Instance.

## Decision

Every root invocation carries one server-derived `ResourceScopeRef` identifying its exact Instance,
Tenant, or Workspace execution scope. That scope is immutable and is copied to every WorkItem,
FlowRun, RuntimeRun, session, assignment, attempt, input request, event, and active Agent-instance
projection created for the invocation. Nested execution inherits the root execution scope exactly.

`TenantId` and `WorkspaceId` are optional denormalized coordinates. `TenantId` is present for Tenant
and Workspace execution; `WorkspaceId` is present only for Workspace execution. Neither an empty
identifier nor the Console's selected Workspace may stand in for an Instance or Tenant scope.
Persistence keys and queries include the canonical scope reference and never infer it from a
definition or principal.

The definition reference independently preserves its exact owner scope, UID, immutable revision or
version, and integrity evidence. Resolution starts from that definition owner and its authorized
ancestors. Invocation scope never causes name rebinding.

Public invocation surfaces accept an explicit target scope only where the caller is allowed to choose
one. The server validates that exact scope against the authenticated principal and derives all
security coordinates. Console and Workplace display the effective execution scope. Execution state
is queried at that exact scope and is not inherited by descendants.

## Consequences

- Instance and Tenant invocations no longer require a synthetic Workspace.
- A Workspace invocation of an ancestor definition remains Workspace-owned and isolated.
- Storage schemas and repository keys must migrate from mandatory Workspace keys to exact scope
  keys in both SQLite and PostgreSQL.
- Existing Workspace execution contracts remain representable without semantic changes.
- APIs that omit an execution scope may default only to the authenticated Workspace context for
  backward-compatible Workspace journeys; they may not silently select Instance or Tenant.
- Authorization, recovery, cancellation, idempotency, audit, and correlation operate on the same
  immutable execution scope.

This decision supersedes ADR-0048, ADR-0176, ADR-0164, and ADR-0179 only where they require durable
execution state to be Workspace-owned. Their definition-ownership, immutable-composition, and
dependency-closure decisions remain in force.
