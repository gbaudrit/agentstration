# ADR-0048: FlowRuns carry a durable execution scope

Status: Accepted — 2026-08-16

## Context

An HTTP request has an authenticated `ClaimsPrincipal` and a resolved Management `RequestContext`, but Flow execution is asynchronous. The in-process queue previously carried only a Run identifier. The background worker therefore had no explicit Tenant, Workspace, or Principal context when an Agent step resolved a Management resource. Relying on request-local state across this boundary is both incorrect and unsafe.

## Decision

Every newly submitted FlowRun records an immutable `FlowRunScope(TenantId, WorkspaceId, PrincipalId)`. HTTP endpoints construct it only from the server-resolved `ICurrentRequestContext`; request identifiers, headers that did not pass principal resolution, and Flow or Pack metadata cannot choose this security scope. The creation contract does not expose `StartedBy`; the server derives the persisted audit label from the resolved Principal.

The queue item carries the Run identifier and a copy of the scope for diagnostics, but the worker treats the persisted FlowRun as authoritative. Immediately before execution it reloads the Principal, Workspace, membership, and effective `runs/execute` permission. Only after successful validation does it push a scoped `RequestContext` for the duration of execution. Revoked permissions, disabled Principals, and disabled Workspaces therefore fail the Run before an Agent is invoked. Validation is asynchronous, while activation of the ambient execution scope is deliberately synchronous after validation; activating it inside an awaited callee would allow `ExecutionContext` restoration to discard the `AsyncLocal` value when that callee returns.

FlowRun read, event, cancellation, draft-run, and run-creation endpoints require contextual ASP.NET Core policies and filter by the complete persisted scope. A caller receives `404` rather than learning that a Run exists in another Workspace. The SQLite repository rejects updates that change a Run scope.

Existing persisted FlowRuns without a scope remain readable only through trusted internal APIs and fail closed if execution is attempted. No relational migration is necessary because FlowRun is stored as a JSON document.

## Consequences

- Pack installation does not grant execution authority and cannot forge identity context; it only installs Flow definitions.
- A delayed Run executes with current authorization, not authorization cached when it was submitted.
- Tenant, Workspace, and Principal provenance survives process and request boundaries.
- Direct Flow API execution is the first complete protected Flow vertical.
- Work/Workplace still uses a separate string Workspace identity. When its submission originates in an initialized Management request context, Work relays that canonical execution scope unchanged to the FlowRun; it never derives security GUIDs from the string Workspace. Complete authorization of the Work/Workplace routes and reconciliation of the two Workspace identities remain follow-up work.
- The separately hosted Workplace forwards only the Agentstration application-cookie chunks and Workspace-selection cookie to the exactly configured API origin, with redirects disabled. The receiving API authenticates the cookie and evaluates `runs/execute`; Workplace does not invent a Principal or trusted header. Flow-backed Work without a resolved scope is rejected before persistence and queueing. This relay is limited to the shipped local, same-session topology; an independently deployed Workplace must use a standard API authentication mechanism such as OAuth Bearer rather than broadening cookie forwarding.
