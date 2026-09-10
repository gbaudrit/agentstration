# ADR-0106 — Flow Run causality is a bounded read model

## Status

Accepted

## Context

Composable orchestration spans a root invocation, nested Flow Runs, Agent steps, and governed Tool attempts. Operators need one diagnostic path, but Flow Runs and Tool lifecycle events already durably own these facts. A second global event store would duplicate state and create consistency and retention problems.

## Decision

- Flow Run causality is reconstructed on demand from the requested run's scoped root, its validated child links, step execution state, and existing Tool lifecycle events.
- The root retains the invocation origin, caller, causation, correlation, and WorkItem identity. Every root and child Run retains whether its exact immutable version was resolved from an active reference.
- The read model is a deterministic breadth-first list with explicit parent Run and parent step identities. It is bounded to 100 nodes per API page and preserves parent identities across page boundaries.
- Tool diagnostics group lifecycle events first by logical `ToolCallId`, then by physical `InvocationId`. Provider identity, attempt state, duration, stable error classification, and governance evaluation count are exposed for deep linking to the existing governance audit.
- The projection is authorized with the complete durable Flow Run scope for every node. Invalid, missing, or cross-scope child links are not traversed.
- Arguments, prompts, Flow inputs and outputs, provider results, error messages, and governance payloads are excluded from this read model regardless of their optional retention elsewhere.

## Consequences

No new persistence, distributed trace backend, or cross-Workspace query is introduced. Diagnostics reflect the durable state already owned by Flow execution and remain available offline. Tree reconstruction performs bounded per-Run reads; a future indexed projection may optimize very large trees without changing the transport contract.
