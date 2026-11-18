# ADR-0209: Knowledge Tools follow the publishing Console scope

## Status

Proposed

## Context

Knowledge Tool exposure still required a Workspace context although Instance and
Tenant administration now have governed operational Workspaces (ADR-0185).
ToolDefinitions and their immutable Tool projections now support logical ownership
in the selected Console scope (ADR-0190 and ADR-0202). The Workspace-only Tool
exposure consequence in ADR-0183 therefore prevents administrative publication
and does not match the present Tool model.

## Decision

Publish Knowledge ToolDefinitions, Tool versions and their ToolSet in the explicit
logical consumer scope selected in the Console: Instance, Tenant or Workspace.
Resolve the Knowledge Source and its pinned retrieval Flow under that same
consumer scope and current authorization. Generated Tools retain the exact source
owner, UID/generation and operation; callers cannot substitute them.

The exposure receipt remains an operational record in the invoking Workspace.
Administrative callers use their existing dedicated operational Workspace for
this receipt and for Flow Runs, Artifacts, Snapshots and retrieval execution.
Only operational resource-store access changes to Workspace context; Storage
availability validation retains the logical consumer context.

This supersedes only the Workspace-only Tool exposure consequence of ADR-0183.
Ordinary Workspace publication keeps its existing ownership. Existing published
Tools and ToolSets remain valid in their original scopes; explicit publication
in another logical scope creates resources there and does not move old ones.
Descendant use remains subject to the existing explicit resource grants.

## Consequences

- Instance and Tenant administration can publish assignable Knowledge Tools in
  their selected scope without sharing operational content with descendants.
- Knowledge tool and ToolSet navigation preserves exact scope and logical context.
- Immutable Tool publication, authorization, approval and source pinning remain
  owned by the existing Tool and Knowledge services.
- Snapshot list/detail/active views validate retained content under the caller's
  logical context, avoiding a false unavailable state after an operational read.
