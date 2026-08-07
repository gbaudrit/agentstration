# Flow definitions

## System-managed Direct Agent Flows

An Entry bound to an Agent is published through a normal immutable Direct Flow. Agentstration owns this Flow, derives its ID and version from the Agent identity and generation, and marks it `systemManaged`. System Flows are excluded from the normal Flow picker and are not independently editable or deletable. FlowRun resolution, execution, events, cancellation, errors, and output normalization remain identical to user-authored Flows.

`Flow` is the root concept describing how work is routed and processed. `WorkItem` captures what must be accomplished, `FlowDefinition` captures the processing strategy, `FlowRun` will represent a concrete Runtime execution, and agents participate in that run.

The Flow module is composed of independent Core, Contracts, Application, Storage.Abstractions, and Storage.Sqlite projects. It is functionally governed by the Management Plane while remaining independent of Management storage internals, Runtime implementations, AI providers, and Microsoft Agent Framework.

## Kinds

- `Direct` selects one typed Agent or AgentType target.
- `Routing` configures a strategy, candidate targets, optional fallback, and extensible settings.
- `Workflow` defines a simple graph of unique nodes, edges, one entry point, and optional outputs.
- `Orchestration` declares participants and a provider-neutral Sequential, Concurrent, Handoff, GroupChat, Magentic, or Custom strategy.
- `Composite` references child Flows without copying their definitions.

Each `spec` is polymorphic and carries the OpenAPI/JSON discriminator `specKind`. References distinguish Agent, AgentType, and Flow targets. A `FlowReference` selects either an immutable semantic version or the active version.

## Versioning and API

`FlowDefinition` is the mutable current definition protected by ETag. Publishing creates an immutable `FlowVersion` snapshot. Activating a version updates only the logical definition's active pointer.

```http
POST   /api/flows
GET    /api/flows
GET    /api/flows/{id}
PUT    /api/flows/{id}
DELETE /api/flows/{id}
GET    /api/flows/{id}/versions
GET    /api/flows/{id}/versions/{version}
POST   /api/flows/{id}/versions
```

Deletion currently removes the logical Flow and its published versions. Direct self-reference is rejected for Composite Flows; indirect recursion analysis is intentionally deferred.

## Runtime boundary

No FlowRun engine exists in this increment. The Runtime will later resolve the WorkItem's FlowReference, compile a provider-neutral execution plan, create a FlowRun, and publish state/checkpoint/result events. Mapping Orchestration strategies to Microsoft Agent Framework belongs in a future Runtime adapter.
