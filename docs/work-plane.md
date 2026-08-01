# Work Plane

The Work Plane receives, represents, and tracks work delegated to agents. It is not a chat model and it does not execute agents directly. Questions, tasks, missions, document processing, conversations, and long-running activities are all represented by the generic `WorkItem` aggregate.

> The Work Plane is where users and systems delegate work to agents, track its lifecycle, interact with ongoing executions, and retrieve results.

## Ownership

The Work Plane owns the functional request, status, requester identity, correlation identifier, interactions, history, result, error, and optimistic version. The Runtime Plane owns agent selection, instantiation, model/tool calls, orchestration, and technical execution.

A WorkItem may optionally hold a lightweight `FlowReference` selecting an exact published version or the active version. It never embeds a Flow definition. Runtime resolution and FlowRun execution are deferred to the Flow Runtime increment.

`Agentstration.Work` has no dependency on Runtime implementations, Microsoft Agent Framework, an AI provider, EF Core, or ASP.NET Core. It exposes `IWorkExecutionGateway`; `Agentstration.Runtime.Local` implements this port for standalone mode. `Agentstration.Work.Storage.Sqlite` persists Work snapshots in its own logical store and indexed table.

## Lifecycle

```text
Pending -> Queued -> Running -> Completed
                        |  \-> Failed
                        |  \-> WaitingForInput -> Running
                        \----> WaitingForApproval -> Running or Failed

Any non-terminal state -> Cancelled
```

Transitions are methods on `WorkItem`; callers cannot assign `Status`. Each Runtime event has a stable `EventId`. Reapplying an event returns without changing the version, history, interactions, or result.

## Public API

The canonical collection is `/api/work/workitems`:

```http
POST /api/work/workitems
GET  /api/work/workitems
GET  /api/work/workitems/{workItemId}
POST /api/work/workitems/{workItemId}/cancel
POST /api/work/workitems/{workItemId}/messages
POST /api/work/workitems/{workItemId}/input
POST /api/work/workitems/{workItemId}/approval
GET  /api/work/workitems/{workItemId}/events
GET  /api/work/workitems/{workItemId}/result
```

Responses use transport contracts rather than domain or EF entities. Entity responses expose an ETag based on the functional version. Mutations accept `If-Match`. Collection queries are bounded to 200 records and support status, type, requester, agent, creation period, sorting, and offset pagination.

## Standalone adapter and limits

The local gateway uses a bounded in-memory queue. After the `Queued` snapshot is durable, a hosted worker invokes the existing Runtime Plane and converts its outcome into `WorkExecutionStarted` and `WorkExecutionCompleted` or `WorkExecutionFailed` events. This adapter is deliberately minimal and will be replaced or complemented by durable Runtime integration.

Current limits include no restart recovery for queued dispatches, no propagation of cancellation to a running agent, no retry/relaunch operation, no external artifact/blob implementation, and no requester authorization. Technical exception details are persisted internally in `WorkError.TechnicalDetails` but are not included in public API error contracts.
