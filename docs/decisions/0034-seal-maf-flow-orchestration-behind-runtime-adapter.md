# ADR 0034: Seal MAF Flow orchestration behind the runtime adapter

## Status

Accepted

## Context

Agentstration needs to execute the sequential, concurrent, handoff, group-chat, and Magentic orchestration patterns supplied by Microsoft Agent Framework (MAF). Flow definitions, REST contracts, persistence, and durable run state must remain stable even when the runtime provider or its SDK changes.

Nothing using the Flow API has been released yet, so the contract can adopt a clean model without a compatibility alias for the earlier `spec` / `specKind` shape.

## Decision

- A Flow resource owns one provider-neutral polymorphic `definition`, discriminated in JSON by `flowKind`.
- Orchestration configuration uses typed provider-neutral pattern records. Public contracts never expose MAF types, namespaces, event objects, executors, or checkpoints.
- `Agentstration.Flow.Application` owns the neutral `IFlowOrchestrationEngine` execution port and neutral streaming events.
- `Agentstration.Runtime.AgentFramework` is the only project allowed to construct MAF agents and workflows or interpret MAF workflow events.
- `FlowRunService` remains the owner of durable run/step state and translates neutral runtime events into persisted Flow events.
- No backward-compatible `spec`, `specKind`, duplicated top-level kind, or storage migration is provided.

## Consequences

The API and stored JSON are smaller and have one source of truth for a Flow kind. MAF upgrades and an alternative runtime remain isolated to an adapter. Adding a new MAF pattern requires a provider-neutral definition and validation before its adapter mapping. Runtime-specific capabilities that cannot yet be represented neutrally must not leak into the public Flow model.
