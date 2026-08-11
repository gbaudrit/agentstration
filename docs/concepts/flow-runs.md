# FlowRuns

A FlowRun is one durable execution of a Flow definition resolved to an immutable published version or captured draft revision. It records status, ordered events, correlation, outputs, cancellation, and parent-run relationships for continuations.

A Flow is reusable configuration; a FlowRun is execution history. A Runtime Run may be created by a FlowRun for an Agent step, but the two records belong to different boundaries.
