# FlowRuns

A FlowRun is one durable execution of a Flow definition resolved to an immutable published version or captured draft revision. It records status, ordered events, correlation, outputs, cancellation, and parent-run relationships for continuations.

The Console presents a semantic execution timeline by default. Consecutive output-delta events from the same step are combined into one bounded streaming-output entry; lifecycle, transition, tool, and failure events remain individual timeline entries. The complete ordered event history remains available in the Raw events view for diagnostics without expanding the page for every streamed fragment.

A Flow is reusable configuration; a FlowRun is execution history. A Runtime Run may be created by a FlowRun for an Agent step, but the two records belong to different boundaries.
