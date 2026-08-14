# Flow definitions

## System-managed Direct Agent Flows

An Entry bound to an Agent is published through a normal immutable Direct Flow. Agentstration owns this Flow, derives its ID and version from the Agent identity and generation, and marks it `systemManaged`. System Flows are excluded from the normal Flow picker and are not independently editable or deletable. FlowRun resolution, execution, events, cancellation, errors, and output normalization remain identical to user-authored Flows.

`Flow` is the root concept describing how work is routed and processed. `WorkItem` captures what must be accomplished, `FlowDefinition` captures the processing strategy, `FlowRun` will represent a concrete Runtime execution, and agents participate in that run.

The Flow module is composed of independent Core, Contracts, Application, Storage.Abstractions, and Storage.Sqlite projects. It is functionally governed by the Management Plane while remaining independent of Management storage internals, Runtime implementations, AI providers, and Microsoft Agent Framework.

## Kinds

The complete behavioral reference and mode-selection guide is available in [Flow modes](concepts/flow-modes.md).

- `Direct` selects one Agent target.
- `Routing` configures a strategy, candidate targets, optional fallback, and extensible settings.
- `Workflow` defines a simple graph of unique nodes, edges, one entry point, and optional outputs.
- `Orchestration` declares participants and a provider-neutral Sequential, Concurrent, Handoff, GroupChat, or Magentic strategy.
- `Composite` references child Flows without copying their definitions.

Each Flow resource exposes one polymorphic `definition` carrying the OpenAPI/JSON discriminator `flowKind`. References distinguish Agent and Flow targets. A `FlowReference` selects either an immutable semantic version or the active version.

Orchestration definitions remain provider-neutral. They describe the participants and one typed strategy (`sequential`, `concurrent`, `handoff`, `groupChat`, or `magentic`). The Microsoft Agent Framework workflow objects are created only inside the runtime adapter and never cross the Flow, API, persistence, or Work Plane boundaries.

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

Flow Runs resolve immutable published definitions and persist their own steps and event history. Typed graph workflows use the local provider-neutral executor; orchestration definitions use the neutral orchestration execution port whose Microsoft Agent Framework implementation is sealed inside the runtime adapter.

Every orchestration Run persists a normalized result containing its strategy, its final output, and the ordered participant results. Participant results retain their turns, resolved agent/model identity, invoked tools, and usage when the provider supplies it. Sequential and Group Chat preserve shared history, Concurrent retains one result per participant, Handoff follows only declared reachable routes, and Magentic uses a distinct manager that is never exposed as a participant.

Execution is bounded by validated participant/iteration/round limits and a server-side timeout. Magentic currently runs autonomously with plan sign-off disabled. A future interactive mode will require a durable neutral request/response contract and resume semantics; until that contract exists, any MAF interaction request fails explicitly instead of leaving a Run hanging.

The operations Console keeps those authoring models distinct. Workflow drafts use the graph designer, while orchestration Flows use a typed editor for participants and the sequential, concurrent, handoff, group-chat, and Magentic strategies. Both experiences share the same Flow details, immutable publication, Run history, and real-time diagnostic surfaces.
