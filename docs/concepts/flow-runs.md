# FlowRuns

A FlowRun is one durable execution of a Flow definition resolved to an immutable published version or captured draft revision. It records status, ordered events, correlation, outputs, cancellation, and parent-run relationships for continuations.

The Console presents a semantic execution timeline by default. Consecutive output-delta events from the same step are combined into one bounded streaming-output entry; lifecycle, transition, tool, and failure events remain individual timeline entries. The complete ordered event history remains available in the Raw events view for diagnostics without expanding the page for every streamed fragment.

A Flow is reusable configuration; a FlowRun is execution history. A Runtime Run may be created by a FlowRun for an Agent step, but the two records belong to different boundaries.

## Durable execution scope

A newly submitted Run stores the canonical Management `TenantId`, `WorkspaceId`, and `PrincipalId` resolved by the server. These values are not accepted from the request body or from Pack metadata, and the persisted scope cannot be changed after creation. The queue is only a delivery mechanism; the persisted Run remains authoritative.

Before starting a queued Run, the worker reloads the Principal and Workspace and re-evaluates the current `runs/execute` permission. It then installs the matching request context only for that execution. A permission revoked after submission therefore prevents execution, and Management resource resolution remains isolated to the Run's Workspace. Run list, detail, event, and cancellation APIs apply the same complete scope and do not reveal cross-Workspace records.

Legacy Runs without this scope fail closed when execution is attempted.
