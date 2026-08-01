# Architecture

## Outcome and constraints

Agentstration starts as one deployable ASP.NET Core process with explicit Management Plane, Runtime Plane, and Work Plane boundaries. The Management Plane is authoritative for definitions, revisions, and desired deployment state. The Runtime Plane owns technical execution. The Work Plane owns the functional lifecycle, history, interactions, and results of delegated work. Runtime `AIAgent` objects are reconstructible and never persisted. The default launch is fully local; Foundry, Aspire, PostgreSQL, Ollama, and OTLP are optional profiles.

## Solution tree

```text
src/
  Agentstration.AppHost/          Aspire orchestration and dashboard
  Agentstration.Web/              REST, Razor Components, MCP, hosted workers
  Agentstration.Web.Components/   reusable Razor components and console design system
  Agentstration.Application/      use cases and module contracts
  Agentstration.Domain/           entities, typed identifiers, domain events
  Agentstration.Evaluation/       offline workflow quality evaluators
  Agentstration.Flow/             provider-neutral Flow definitions and references
  Agentstration.Flow.Application/ Flow CRUD, publication, activation, resolution
  Agentstration.Flow.Contracts/   public Flow API contracts
  Agentstration.Flow.Storage.Abstractions/
  Agentstration.Flow.Storage.Sqlite/
  Agentstration.Contracts/        transport-neutral request/response contracts
  Agentstration.Infrastructure/   JSON/EF storage, AI, HTTP, event bus, queues
  Agentstration.Management.Abstractions/ canonical resources, ports, events, resolved specs
  Agentstration.Management.Core/  Management validation, use cases, revisions, deployments
  Agentstration.Management.Contracts/
  Agentstration.Management.Storage.Sqlite/
  Agentstration.ModelProviders/      provider-neutral model-provider resolution
  Agentstration.ModelProviders.Ollama/ OllamaSharp IChatClient adapter
  Agentstration.Runtime.Abstractions/
  Agentstration.Runtime.Core/       Runtime Run lifecycle, observation, cancellation
  Agentstration.Runtime.Contracts/  Public Runtime Run HTTP contracts
  Agentstration.Runtime.AgentFramework/
  Agentstration.Runtime.Local/
  Agentstration.Runtime.Storage.Sqlite/
  Agentstration.Work/              WorkItem aggregate, execution event contracts, runtime port
  Agentstration.Work.Contracts/    versionable HTTP request/response contracts
  Agentstration.Work.Storage.Abstractions/
  Agentstration.Work.Storage.Sqlite/
tests/
  Agentstration.Application.Tests/
  Agentstration.ArchitectureTests/
  Agentstration.Evaluation.Tests/
  Agentstration.Management.Tests/
  Agentstration.Web.Tests/
  Agentstration.Web.Components.Tests/
docs/decisions/
```

Core dependency direction:

```text
Web ---> Infrastructure ---> Application ---> Contracts ---> Domain
  \-----------------------------> Application -------------> Domain

Web -> Management contracts + core
Management.Core -> Management.Abstractions + Runtime.Abstractions
Management.Contracts -> Management.Abstractions
Management.Storage.Sqlite -> Management.Abstractions + EF Core SQLite
Infrastructure -> SQLite control-plane storage + local/MAF runtime adapters
Web -> ModelProviders.Ollama -> ModelProviders + Microsoft.Extensions.AI
AppHost -> Aspire Ollama hosting integration
Runtime.AgentFramework -> runtime abstractions + Microsoft Agent Framework
Application -> Work + Work storage abstractions
Flow.Application -> Flow + Flow.Storage.Abstractions
Flow.Storage.Sqlite -> Flow.Storage.Abstractions + EF Core SQLite
Runtime.Local -> Work execution port
Runtime.Core -> Runtime.Abstractions + Management.Abstractions
Runtime.Storage.Sqlite -> Runtime.Abstractions + EF Core SQLite
Work.Storage.Sqlite -> Work storage abstractions + EF Core SQLite
```

`Domain` contains no Management types and has no framework dependency. Canonical Management resources and provider-neutral ports live in `Management.Abstractions`; validation and use cases live in `Management.Core`. SQLite and EF Core are confined to `Management.Storage.Sqlite`. Concrete `AIAgent` types are confined to `Runtime.AgentFramework`. Foundry is absent from every central project.

## Module responsibilities

| Module | Current responsibility | Planned extension |
|---|---|---|
| Management plane | canonical declarative agent resources, typed references, desired state, generations, provisioning status, lifecycle events, deterministic revisions, deployments, ETag API | operations, policies, provider-backed existence resolution, connections, identities, manifest import |
| Control storage | SQLite JSON resources with indexed metadata and optimistic concurrency | richer relational projections and migrations |
| Runtime plane | durable Run resources and events, SSE observation, cancellation/retry, MAF `ChatClientAgent`, in-process/shared-host provisioning, registry, reconciliation | provider-native token/tool streaming, sessions, dedicated hosts, containers, remote and Foundry adapters |
| Model providers | provider-neutral resolver plus local OllamaSharp adapter exposed as `IChatClient`; Aspire provisions the development container and model | additional local or remote adapters selected at composition time |
| Work plane | `WorkItem` lifecycle, interactions, idempotent runtime events, results, canonical REST API | durable dispatch, retry/recovery, requester authorization, artifact storage |
| Work storage | independent SQLite snapshots, indexed query fields, optimistic version concurrency | migrations and richer projections |
| Flow definitions | Direct, Routing, Workflow, Orchestration, Composite specifications; immutable published versions | FlowRun compilation and execution adapters |
| Flow storage | independent readable JSON documents, ETags, active/current definition separation | migrations, indirect reference projections |
| Identity | local-user boundary only | OIDC, users, members, workspace authorization |
| Workspaces | workspace and inbox lifecycle | teams, organizations, policies |
| Ingestion | text, JSON, multipart file, URL, hash deduplication | webhooks, email, connectors |
| Memory | normalized content, summaries, categories, search contract | facts, relations, embeddings, conversations |
| Routing | deterministic stateless decision | rule catalog and LLM router |
| Agents | management definitions plus isolated MAF runtime adapter | sessions, execution budgets, richer tool policies |
| Workflows | normalize → analyze → remember | parallel, routing, handoff, supervisor, HITL |
| Scheduling | standalone polling worker | Quartz persistent scheduler |
| Tools | safe URL reader and deterministic observation | connector/tool catalog and permissions |
| Notifications | internal notification record/event | email, Teams, webhook channels |
| MCP | nine tools reusing application services | resources and authorization |
| Evaluation | deterministic `Microsoft.Extensions.AI.Evaluation` metrics and versioned content-workflow dataset | LLM-as-judge quality/safety evaluators and reports |
| Observability | OTel traces/metrics/log correlation tags | dashboards, SLOs, evaluation telemetry |

## Principal contracts

```csharp
public interface IIntentRouter
{
    ValueTask<RoutingDecision> RouteAsync(RoutingContext context, CancellationToken cancellationToken);
}

public interface IAgentRuntime
{
    Task<AgentExecutionResult> RunAsync(AgentExecutionRequest request, CancellationToken cancellationToken);
}

public interface IMemoryStore { Task AddAsync(MemoryEntry entry, CancellationToken cancellationToken); }
public interface IMemorySearch { Task<IReadOnlyList<MemoryEntry>> SearchAsync(WorkspaceId workspaceId, string query, int limit, CancellationToken cancellationToken); }
public interface IBlobStore { Task<string> PutAsync(WorkspaceId workspaceId, string name, Stream content, CancellationToken cancellationToken); }
public interface IEmbeddingStore { Task UpsertAsync(WorkspaceId workspaceId, Guid id, ReadOnlyMemory<float> embedding, CancellationToken cancellationToken); }
public interface IScheduler { Task TriggerDueMissionsAsync(CancellationToken cancellationToken); }
```

Other important contracts are `IPlatformStore`, `IEventBus`, `IEventHandler<T>`, `IItemProcessingQueue`, `IContentSourceReader`, and `IObservationTool`. Expected business failures use `Result<T>`; unexpected infrastructure failures remain exceptions and are translated at the HTTP boundary.

## Initial data model

The executable model includes `Workspace`, `Inbox`, `Item`, `RawContent`, `NormalizedContent`, `MemoryEntry`, `Mission`, `MissionRun`, `Notification`, and `AuditEntry`. The wider model reserves `User`, `WorkspaceMember`, `AgentDefinition`, `AgentRun`, `WorkflowDefinition`, `WorkflowRun`, `Schedule`, and `ToolDefinition` for later increments.

Every workspace-owned record carries `WorkspaceId`. Queries require it alongside the entity identifier. Key indexes in the PostgreSQL model cover `(WorkspaceId, Slug)`, `(WorkspaceId, InboxId, ContentHash)`, `(WorkspaceId, Status, CreatedAt)`, `(WorkspaceId, ItemId, CreatedAt)`, and `(WorkspaceId, MissionId, StartedAt)`.

Raw content is append-only from the workflow's perspective. Normalization and AI results are separate records. Content hash plus inbox scope provides ingestion idempotency.

## Main flows

### Content vertical

```text
REST / UI / MCP
  -> IngestionService validates, hashes, saves raw source
  -> ItemReceived through in-process event bus
  -> bounded Channel queue (HTTP returns 202)
  -> ItemProcessingWorker
  -> deterministic router
  -> normalize
  -> IAgentRuntime -> IChatClient
  -> MemoryEntry
  -> ItemProcessed
```

### Monitoring vertical

```text
REST / UI / MCP / scheduler tick
  -> MissionService
  -> IObservationTool (demo sequence in MVP)
  -> MissionRun + observation MemoryEntry
  -> compare previous observation
  -> threshold satisfied and changed
  -> Notification + NotificationRequested
```

### Work vertical

```text
POST /api/work/workitems
  -> WorkItemService creates and persists Pending WorkItem
  -> IWorkExecutionGateway accepts the request
  -> WorkItem is persisted as Queued
  -> LocalWorkExecutionWorker delegates execution to the Runtime Plane
  -> idempotent WorkExecutionStarted and WorkExecutionCompleted/Failed events
  -> SQLite Work snapshot, immutable functional history, result
  -> GET /api/work/workitems/{id} and /result
```

The local queue is a standalone adapter, not the final distributed integration. It will be replaced or complemented by a durable Runtime connector without changing the Work aggregate, application service, or public API contracts.

### Flow definition vertical

```text
POST /api/flows
  -> FlowService validates the discriminated specification
  -> mutable current FlowDefinition persisted with ETag
POST /api/flows/{id}/versions
  -> immutable FlowVersion snapshot
  -> optional active-version pointer update
WorkItem -> optional FlowReference (exact or active)
Runtime  -> future FlowReference resolution -> FlowRun
```

The Flow module is physically independent but belongs functionally to the Management Plane. It models orchestration strategies without referencing Microsoft Agent Framework and deliberately contains no graph scheduler or FlowRun engine.

### Declarative agent vertical

```text
PUT canonical Agentstration.Agents/agents resource
  -> validate route/body identity, schema, API version, and typed resource references
  -> compare only desired state with the stored declaration
  -> preserve generation and ETag when identical, otherwise increment generation
  -> persist through the generic SQLite control-plane store
  -> publish AgentCreated or AgentUpdated (DELETE publishes AgentDeleted)
  -> Runtime may fetch the canonical resource/revision and materialize it
```

Management never constructs an `AIAgent`, resolves credentials, injects a model client, instantiates tools, or executes an agent. `ResolvedAgentSpec` is the provider-neutral boundary prepared for future resolution of `AgentType`, model profile, and tools; concrete MAF materialization remains in `Agentstration.Runtime.AgentFramework`.

### Runtime Run vertical

```text
Console / API / future Work or Flow adapter
  -> POST Runtime Run returns 202 Accepted
  -> bounded local execution queue
  -> resolve exact managed agent generation and ready deployment
  -> IRuntimeRegistry executes the already materialized agent
  -> persist ordered status, trace, response, tool and terminal events
  -> SSE /events with Last-Event-ID resumption
  -> terminal Run remains queryable and retry creates a new Run ID
```

An interactive console Run is owned entirely by the Runtime Plane and does not create a Work Item. Runtime Run storage is independent from Management and Work storage.

### Local model provider flow

```text
AppHost --provisions--> Ollama container + persistent model volume
   |--injects connection--> Web composition
Web -> ModelProviders.Ollama -> OllamaSharp IChatClient
Runtime.AgentFramework -> IChatClient (provider-neutral)
```

The standalone Web process still defaults to the deterministic client. Selecting `AI:Provider=Ollama` activates the Ollama provider adapter; the Agent Runner then exercises it through the normal Runtime path. The development-only diagnostic endpoint checks connectivity without becoming a second business workflow. Prompts and model responses are not written to logs.

### URL security flow

The reader only accepts absolute HTTP(S), rejects credentials, loopback, private/link-local/multicast addresses after DNS resolution, uses a 15-second timeout, streams headers first, and enforces a 2 MiB limit. Redirect revalidation is a planned hardening item; public deployment should disable automatic redirects until each hop is checked.

## Observability

Activity sources exist for ingestion, workflows, and missions. Spans carry workspace/item/mission identifiers, but raw documents and prompts are never logged. ASP.NET Core and HttpClient traces/metrics export to OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set; Aspire supplies the local dashboard endpoint.

## Implementation plan

1. **Delivered foundation:** solution conventions, domain/application boundaries, local content store, API/UI/MCP, OTel, Aspire, and tests.
2. **Delivered management vertical:** agent types, policy-aware deterministic compilation, immutable revisions, SQLite control-plane storage, deployments, ETags, Microsoft-like REST API, and pagination.
3. **Delivered runtime vertical:** isolated Microsoft Agent Framework adapter, in-process/shared-host provisioners, runtime registry, periodic reconciliation, single-agent routing, execution, and standalone sample data.
4. **Delivered content and monitoring verticals:** ingestion, memory/search, deterministic/OpenAI-compatible AI, missions, change detection, and internal notifications.
5. **Delivered Work vertical:** domain-controlled lifecycle, typed identifiers, interactions, idempotent Runtime events, independent SQLite persistence, local execution gateway, canonical REST API, metrics, traces, and tests.
6. **Delivered Flow definition vertical:** independent projects, five discriminated kinds, graph/reference validation, CRUD, ETags, immutable versions, active resolution, Work references, OpenAPI, and SQLite.
7. **Next Flow Runtime increment:** FlowRun contracts, compiled execution plan, state/checkpoints, deterministic routing and simple workflow execution.
8. **Next Work increment:** durable execution dispatch/recovery, requester authorization, external artifact storage, cancel propagation, and retry/relaunch operations.
9. **Delivered Runtime Run increment:** durable Run resources, local queue, exact agent-generation resolution, SQLite history, SSE observation, cancellation, retry, and Agent Runner console.
10. **Next management increment:** durable long-running operations, manifest importer, resource groups, model/tool/connection/identity providers, and management authentication.
11. **Next runtime increment:** provider-native streaming and tool telemetry, session storage, tool catalog policies, revision traffic splitting, dedicated process/container and remote endpoint adapters.
12. **Delivered local model-provider increment:** provider-neutral resolver, OllamaSharp adapter, Aspire-provisioned Ollama/model volume, Runner integration, development diagnostic, and offline tests.

## ADR catalog

- ADR-0001: modular monolith first
- ADR-0002: local JSON default and PostgreSQL target
- ADR-0003: Microsoft.Extensions.AI boundary and Agent Framework adapter
- ADR-0004: standalone scheduler before Quartz
- ADR-0005: one application service layer for REST, UI, and MCP
- ADR-0009: independent Work Plane with local Runtime dispatch
- ADR-0010: independent Flow definition module
- ADR-0011: dedicated Management module
- ADR-0012: durable Runtime Run resource and observable execution
- ADR-0013: model-provider boundary and local Ollama adapter
