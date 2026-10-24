# ADR-0182: Administrative execution uses governed operational Workspaces

Status: Accepted — 2026-10-24

## Context

Instance- and Tenant-owned definitions need an execution destination when an administrator operates
from the corresponding Console context. Persisting Runs directly at `/instance` or
`/tenants/{id}` would require every execution subsystem, query surface, artifact store, conversation,
and recovery path to implement a second non-Workspace ownership model. Reusing the current or first
business Workspace instead makes administrative state depend on an arbitrary business boundary and
can expose it to unrelated members.

ADR-0177 selected exact Instance and Tenant execution-state ownership to avoid synthetic Workspaces.
The Console context work later established that administrative scope and operational placement are
separate. The required destinations are not aliases or synthetic identifiers: they are real,
governed Workspaces with the same isolation and durable lifecycle as every other execution boundary.

## Decision

- Agentstration owns one reserved system Tenant with one protected Instance operations Workspace.
- Every active ordinary Tenant owns exactly one protected Tenant operations Workspace. It is created
  automatically for new Tenants and reconciled idempotently for existing Tenants during startup.
- Reserved topology uses stable code-owned identities and names. A collision with those identities or
  names fails explicitly; ordinary creation, selection as a business Workspace, membership mutation,
  rename, deletion, transfer, and reassignment are not allowed.
- Selecting the Instance Console context places operational work in the Instance operations Workspace.
  Selecting a Tenant Console context places it in that Tenant's operations Workspace. Selecting a
  business Workspace continues to place work in that exact Workspace.
- Operational Workspaces are not ordinary business Workspace choices. Access is reached through the
  corresponding authorized administrative context: platform authorization for Instance operations,
  and Tenant authorization for Tenant operations.
- WorkItems, Flow Runs, Runtime Runs, sessions, assignments, input requests, artifacts, and
  conversations remain Workspace-owned. Execution evidence independently retains the exact owner
  scope and immutable identity of each Instance- or Tenant-owned definition.
- The system Tenant is an administrative topology boundary only. It is not an ancestor of business
  Tenants, and its operational state is never inherited by them.

## Consequences

- Existing Workspace-scoped execution, recovery, storage, and presentation paths remain authoritative;
  no parallel Instance/Tenant execution store is required.
- Instance and Tenant administration no longer depends on whichever business Workspace happened to be
  selected previously.
- Every Tenant has one additional protected Workspace whose platform resources are provisioned by the
  same idempotent lifecycle as other Workspaces.
- Operational topology must be reconciled before an Instance or Tenant Console context can be selected.
- ADR-0177 is superseded where it requires direct durable Instance- or Tenant-owned execution state.
  Its exact definition identity, immutable scope propagation, authorization, and non-rebinding rules
  remain accepted. ADR-0181's separation between administrative context and operational placement is
  now concretely implemented by these Workspaces.
