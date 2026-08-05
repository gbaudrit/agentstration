# ADR-0019: Flow-owned Run resource and execution console

## Decision

Flow execution is represented by a durable `FlowRun` owned by the Flow module, independently from direct agent Runtime Runs and Work Items. A Run stores an immutable snapshot of either an exact published `FlowVersion` or an exact draft revision and definition hash. The sequential executor supports typed `Input`, `Agent`, `Router`, `Condition`, `Transform`, `Output`, and `Failure` steps.

Flow Application owns draft authoring, validation, constrained expression evaluation, orchestration, queue/cancellation, event publication, and agent-execution ports. Infrastructure supplies Management resource resolution, the bounded local queue, cancellation registry, and adapter to the existing managed-agent runtime. SQLite persists drafts, immutable versions, Runs, step state, and ordered differential events in the independent Flow database. HTTP creation is asynchronous, active Runs can be cancelled, and SignalR streams persisted events with sequence-based replay.

The console makes Flow the primary execution experience. It provides creation templates, a visual editor with Designer/Definition/Split modes, palette/canvas/inspector/validation zones, undo/redo, drag persistence, automatic layout, YAML/JSON editing, draft validation and publication, global Run history, and a diagnostic Run page that overlays state on the executed snapshot.

## Consequences

Historical Runs remain interpretable after the draft or active version changes. Work may later link to a Flow Run without owning or duplicating its detailed trace. Microsoft Agent Framework types remain absent from Flow contracts and models.

This increment deliberately does not add parallelism, loops, waits, approvals, subflows, arbitrary code expressions, or provider-specific Flow contracts. The expression language is intentionally limited to input/step-output paths and comparisons. Sensitive input/output redaction policies, durable distributed dispatch, richer deployment resources, and collaborative editing remain later increments.
