# ADR-0012: Runtime Run resource and observable execution

## Decision

Interactive and programmatic agent executions are represented by durable Runtime `Run` resources. `Agentstration.Runtime.Abstractions` owns the provider-neutral Run model and ports, `Agentstration.Runtime.Core` owns lifecycle and execution orchestration, `Agentstration.Runtime.Contracts` owns public HTTP contracts, and `Agentstration.Runtime.Storage.Sqlite` persists Runs and their ordered events.

A Run references an exact managed agent resource generation. Runtime resolves a ready deployment and executes it through `IRuntimeRegistry`; it never constructs a concrete agent. Creation is asynchronous, observation uses Server-Sent Events, cancellation is explicit, and retry creates a new Run. Direct interactive Runs do not create Work Items.

## Consequences

Web, API, Work, and future Flow entry points can converge on the same Runtime Run lifecycle. The local default gains an independent `runtime-plane.db`. SSE is initially backed by persisted event polling, which supports reconnection through `Last-Event-ID` without introducing WebSockets or an external broker. Provider-specific streaming, detailed token metrics, tool-call interception, and authorization-based trace redaction remain adapter-level extensions.
