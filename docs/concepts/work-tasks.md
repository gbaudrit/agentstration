# WorkTasks and Work Items

`WorkItem` is the Work Plane aggregate that owns the functional request, lifecycle, history, interactions, result, error, and optimistic version. It delegates technical execution through `IWorkExecutionGateway`.

`WorkTask` is the Workplace-facing projection of asynchronous work. Continuations may use child Work Items internally while remaining one living Task to the user. The operations Console supervises Tasks through Work API rather than reading Work storage directly.

See the [Work Plane](../work-plane.md) and [ADR-0023](../decisions/0023-console-supervises-worktasks-through-work-api.md).
