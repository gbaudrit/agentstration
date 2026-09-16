# ADR-0113 — Entry exposure separates ownership from presentation

## Status

Accepted

## Context

Entries are Workspace-owned publication points. Workplace needs to present an Entry in its owning Workspace space and may explicitly promote selected Entries to Tenant home. Console also needs Entries that can be presented independently from the Workspace currently being administered. Moving Entries or their target resources to Tenant scope, or adding a Console-specific Entry kind, would conflate ownership, execution, and presentation.

## Decision

Every Entry remains owned by exactly one Workspace. A versioned `EntryExposure` policy declares product surfaces independently from Workplace placement. Version 1 supports the `Workplace` and `Console` surfaces plus the `OwningSpace` and `TenantHome` Workplace placements.

Existing persisted and declarative Entries that omit exposure receive the deterministic `Workplace` plus `OwningSpace` default. Authoring rejects an unsupported policy version, unknown enum value, duplicate value, empty surface list, or a Workplace placement inconsistent with the Workplace surface. Discovery and invocation also evaluate the policy defensively and fail closed for unsupported persisted values.

`GET /api/entries` is the shared discovery projection. Its compatibility default is `surface=Workplace&placement=OwningSpace`, which searches only the current Workspace. `surface=Workplace&placement=TenantHome` and `surface=Console` may search all readable Workspaces in the current Tenant. The discovery service obtains an authorization-filtered Workspace set and verifies that every returned Entry still declares that Workspace as its owner.

Invocation carries the requested surface and placement into the shared Workplace application service. Owning-space Workplace invocation additionally requires a published Dashboard reference. Tenant-home and Console presentation do not transfer resource visibility: invocation loads the Entry by its owning Workspace, and Flow resolution and execution continue within that same Workspace. HTTP Workspace routes still select and authorize the owner before invoking.

## Consequences

Console and Workplace consume one exposure contract without parallel flags. Tenant-rooted Workplace and Console-assistant features can build their shells on the shared discovery API while preserving Workspace isolation. A caller who can read one Workspace does not discover exposed Entries from an unreadable sibling Workspace, and exposure never grants access to Agents, Flows, Tools, conversations, or execution state.

The JSON document stores require no relational migration because exposure is embedded in the existing Entry payload. Rewriting or republishing an older Entry materializes the explicit version 1 default.
