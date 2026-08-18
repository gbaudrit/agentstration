# ADR-0058: Tool governance decisions are traced per physical attempt

## Status

Accepted

## Context

Tool execution is durable and at-least-once. A logical Tool call may therefore have several physical invocations after retry, replay or checkpoint recovery. Tool Hook resources are resolved again for every physical invocation and can change generation between attempts.

The terminal Tool outcome alone cannot explain which governance configuration allowed or denied a particular external effect. Persisting Tool arguments or provider results for this purpose would unnecessarily expand the sensitive-data surface.

## Decision

After resolving and evaluating the ordered pre-invocation hook chain, the Tool execution pipeline emits a provider-neutral `ToolExecutionGovernanceEvaluated` lifecycle fact before calling `IToolInvoker`.

For each evaluated hook, the fact contains only:

- its stable runtime hook identifier and order;
- whether it is local or backed by a Management resource;
- the Management resource identity and generation when applicable;
- the `Allowed`, `Denied` or `Failed` decision;
- a stable refusal or failure code when applicable.

Arguments, provider results and human-readable denial messages are excluded. The lifecycle fact retains the logical `ToolCallId` and physical `InvocationId` from `ToolExecutionContext`.

Governance projection is part of the fail-closed pre-invocation boundary. If the fact cannot be durably projected, the provider is not invoked and the call fails as a hook-governance failure. Runtime Runs expose the latest physical attempt's governance trace on `RuntimeToolCall`; their append-only event history and the Flow Run journal retain the facts for individual attempts.

The trace describes the policy actually evaluated for an attempt. It is not a frozen policy snapshot, an idempotency record or an exactly-once guarantee.

## Consequences

- Operators can distinguish a logical call from each retry and identify the exact HookResource generation that governed it.
- Calls with no matching hooks still emit an empty governance fact before provider invocation.
- A denial trace is durable before the denied terminal lifecycle fact is emitted.
- Updating a HookResource may legitimately produce a different trace on the next physical attempt.
- Post-invocation notifications remain lifecycle behavior and do not rewrite the pre-invocation governance decision.
- A workspace-scoped read model queries the existing Runtime and Flow journals by owner and Run, with sequence pagination and Tool, Hook or decision filters. It does not create a second audit store.
- A dedicated Console view, cross-Run indexing, retention controls, cryptographic integrity and capture of transformed payloads remain future work.
