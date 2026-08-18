# FlowRuns

A FlowRun is one durable execution of a Flow definition resolved to an immutable published version or captured draft revision. It records status, ordered events, correlation, outputs, cancellation, and parent-run relationships for continuations.

The Console presents a semantic execution timeline by default. Consecutive output-delta events from the same step are combined into one bounded streaming-output entry; lifecycle, transition, tool, and failure events remain individual timeline entries. The complete ordered event history remains available in the Raw events view for diagnostics without expanding the page for every streamed fragment.

A Flow is reusable configuration; a FlowRun is execution history. A Runtime Run may be created by a FlowRun for an Agent step, but the two records belong to different boundaries.

## Durable execution scope

A newly submitted Run stores the canonical Management `TenantId`, `WorkspaceId`, and `PrincipalId` resolved by the server. These values are not accepted from the request body or from Pack metadata, and the persisted scope cannot be changed after creation. The queue is only a delivery mechanism; the persisted Run remains authoritative.

Before starting a queued Run, the worker reloads the Principal and Workspace and re-evaluates the current `runs/execute` permission. It then installs the matching request context only for that execution. A permission revoked after submission therefore prevents execution, and Management resource resolution remains isolated to the Run's Workspace. Run list, detail, event, and cancellation APIs apply the same complete scope and do not reveal cross-Workspace records.

Legacy Runs without this scope fail closed when execution is attempted.

## Interactive suspension and recovery

An orchestration may request external text, a choice, or a confirmation. Agentstration persists that request independently from the UI and changes the Run from `Running` to the non-terminal `WaitingForInput` state. The request records its source, options, expiry, response value, response time, and responding Principal.

The REST surface is:

```http
GET  /api/flowRuns/{runId}/inputs
GET  /api/flowRuns/{runId}/inputs/{inputId}
POST /api/flowRuns/{runId}/inputs/{inputId}/response
```

Posting a valid response stores it once through optimistic concurrency, requeues the Run, and returns `202 Accepted`. A second response conflicts. An unanswered request that reaches `ExpiresAt` becomes `Expired`, and its Run becomes `TimedOut` with an explicit reason.

The Run's captured definition and immutable runtime bindings are authoritative during continuation. The runtime adapter rebuilds the same participant revisions and restores its opaque SQLite-backed execution state; it never resolves the newest Flow or Agent revision. Startup recovery repairs answered-but-not-requeued Runs and expired execution leases. Execution remains at-least-once, so external tool providers must use stable operation identifiers when they offer idempotent effects.
