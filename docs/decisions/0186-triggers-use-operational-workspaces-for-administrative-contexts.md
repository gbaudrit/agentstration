# ADR-0186: Triggers use operational Workspaces for administrative contexts

Status: Accepted — 2026-10-25

## Context

ADR-0068 made Trigger definitions and occurrence state Workspace-owned. ADR-0185 later introduced
governed operational Workspaces so Instance and Tenant administration can reuse the durable execution
plane without assigning administrative state to an arbitrary business Workspace.

A Trigger created while the Console is in an Instance or Tenant context therefore needs two distinct
coordinates. Its durable resource, Quartz projection, occurrence history, Work and Runs need a physical
Workspace owner. Its Flow visibility, authorization and audit decisions need the selected logical
Instance or Tenant scope. Treating the hidden operational Workspace as the consumer scope would bypass
the explicit descendant access policy. Treating the Trigger as Instance- or Tenant-owned would create a
second scheduler and occurrence ownership model.

## Decision

- Trigger resources remain Workspace-owned. In an Instance or Tenant administrative context they are
  owned by that context's governed operational Workspace; in a Workspace context they remain owned by
  the selected Workspace.
- The API resolves the operational Workspace from the authenticated context. Clients cannot select or
  override another Trigger ownership scope.
- A Trigger execution identity stores the physical Tenant and Workspace coordinates, the Principal,
  and the independent logical Instance, Tenant or Workspace authorization scope.
- Trigger Flow targets are resolved from the logical scope. Saving a Trigger requires `Discover`;
  enabling or firing it additionally requires `Use`. Same-scope access follows the resource access
  policy's existing same-scope rule.
- The resolved target persists its exact Flow owner scope, namespace, UID, published version and
  definition hash. Firing fails closed when that identity is missing, unauthorized or stale; it never
  falls back to a homonymous Flow.
- Quartz job data includes the operational Workspace ID. Scheduler recovery and firing load the Trigger
  by its exact Workspace, namespace and name, so homonymous Triggers remain isolated.
- Work and Flow Runs remain physically owned by the operational Workspace while their execution scope
  retains the logical authorization scope established by ADR-0185.
- Console surfaces present the Flow definition scope and operational Workspace separately. Operational
  Workspace ownership is informative and read-only, not a resource-scope selector.

## Consequences

- Existing Workspace Trigger resources, schedules and occurrence history keep their ownership model.
- Instance and Tenant administrators can manage schedules without exposing protected operational
  Workspaces as ordinary Workspace choices.
- Access revocation takes effect at the next enable or firing operation, including after scheduler
  recovery.
- Trigger target manifests gain optional immutable identity fields. Existing development data may be
  reseeded; no relational migration is required because Trigger payloads remain JSON resources.
- ADR-0068 is superseded only where it limits Trigger creation and target validation to an interactive
  Workspace context. Its Work-submission, occurrence idempotency and reconstructible Quartz projection
  decisions remain accepted.
