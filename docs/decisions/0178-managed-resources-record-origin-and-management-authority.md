# ADR-0178: Managed resources record origin and management authority

Status: Accepted — 2026-10-19

## Context

Agentstration resources may be authored by an operator, reconciled by the product, installed by a
Pack, or contributed by an extension. Existing annotations describe some of those sources, but they
are user-controlled metadata and therefore cannot protect resource content or lifecycle. This lets
an ordinary update make a reconciled resource diverge from its source and gives the Console no
authoritative way to decide whether editing is allowed.

Ownership scope, descendant access, and management provenance answer different questions. An
Instance resource may be visible or usable from a descendant while remaining exclusively managed
by its originating installation. Conversely, an operator-authored Instance resource remains
editable according to ordinary authorization.

## Decision

Every mutable Management resource and Flow definition carries structured governance with:

- an `Origin` kind (`user`, `builtIn`, `pack`, or `extension`) plus an optional stable reference and
  supplied version;
- an optional `ManagementAuthority` naming the exact Core component, Pack installation, or
  extension installation that owns content and lifecycle reconciliation.

Absence of a management authority means ordinary authorization governs create, update, and delete.
When an authority is present, only an active reconciliation context with the same authority kind and
stable reference may update or delete the resource. A caller cannot assign managed governance,
adopt an existing operator resource through update, replace the authority, or remove it. A matching
manager may advance the recorded supplied version during reconciliation.

Denied ordinary mutations and successful trusted reconciliations emit structured security-audit
events containing only outcome and stable reason evidence, never resource content.

Core, Pack, and extension reconciliation establish the authority in-process around the existing
application use cases. Storage enforces the invariant for generic Management resources, while the
Flow application service enforces it for the independent Flow store. AEP contribution
materialization records the exact extension registration, advertised identity, and supplied version.

Governance is system-owned resource state. Descriptive annotations remain available for display and
compatibility but are not authoritative. Bootstrap is an application mechanism and does not by
itself become an origin or manager.

Access policies remain separate administrative overlays under ADR-0172. This decision does not add
a mutation-policy language, generic capabilities, user/group RBAC, or Network, System, Guardrail,
or other policy families.

## Consequences

- Built-in and Pack-managed content is read-only outside its exact reconciler, independent of scope.
- Operator-authored resources at Instance, Tenant, or Workspace scope remain editable under normal
  authorization.
- APIs and Console clients can project the authoritative origin and manager without inferring them
  from names or annotations.
- Reconciliation can update its own resources and supplied version but cannot take over another
  manager's resource or an existing operator resource.
- Pre-release local databases may be reseeded; existing records without governance deserialize as
  operator-authored resources.

This decision refines ADR-0069: the `agentstration.io/builtin` annotation remains descriptive, while
the system-owned governance field is the authorization and integrity boundary. It complements
ADR-0037 Pack inventory and ADR-0172 access overlays without changing their responsibilities.
