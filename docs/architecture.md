# Architecture

## Outcome and constraints

Agentstration keeps explicit Management Plane, Runtime Plane, and Work Plane boundaries in one modular codebase and one authoritative standalone server. `Agentstration.Web` hosts the operations Console and all server-side API surfaces; the end-user Workplace remains a separate HTTP/SignalR client UI. The Management Plane is authoritative for definitions, revisions, and desired deployment state. The Runtime Plane owns technical execution. The Work Plane owns the functional lifecycle, history, interactions, and results of delegated work. Runtime `AIAgent` objects are reconstructible and never persisted. The default launch is fully local; Foundry, PostgreSQL, Ollama, and OTLP are optional profiles, while Aspire orchestrates the server, Workplace, and optional extensions.

## Solution tree

```text
src/
  Agentstration.AppHost/          Aspire orchestration and dashboard
  Agentstration.Web/              authoritative server, operations Console, REST, MCP, workers and hubs
  Agentstration.Web.Components/   reusable Razor components and console design system
  Agentstration.Web.FlowDesigner/ Flow-specific Razor UI, editor state, Z diagrams, Monaco
  Agentstration.Workplace.Client/ typed HTTP and reconnecting SignalR client
  Agentstration.Workplace.Components/ reusable Workplace business components
  Agentstration.Workplace.Web/    standalone end-user Blazor host
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
  ../aep/                            autonomous future AEP repository subtree
  Agentstration.Extensions.Ollama/   autonomous AEP-to-Ollama service
  Agentstration.ModelProviders/      provider-neutral model-provider resolution through AEP
  Agentstration.Tools.Mcp/           Tool catalog, AEP-to-MCP resolution, official MCP client
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
  Agentstration.Web.FlowDesigner.Tests/
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
Web -> ModelProviders -> Aep.MicrosoftExtensionsAI -> Aep.Client
Extensions.Ollama -> Aep.AspNetCore + OllamaSharp
AppHost -> Extensions.Ollama (configured local Ollama endpoint)
Runtime.AgentFramework -> runtime abstractions + ModelProviders + Microsoft Agent Framework
Application -> Work + Work storage abstractions
Flow.Application -> Flow + Flow.Storage.Abstractions
Flow.Storage.Sqlite -> Flow.Storage.Abstractions + EF Core SQLite
Web.FlowDesigner -> Web.Components + Flow + Flow.Contracts
Web -> Web.FlowDesigner (host adapters implement designer backend/resource ports)
Runtime.Local -> Work execution port
Runtime.Core -> Runtime.Abstractions
Runtime.Storage.Sqlite -> Runtime.Abstractions + EF Core SQLite
Work.Storage.Sqlite -> Work storage abstractions + EF Core SQLite
```

`Domain` contains no Management types and has no framework dependency. Canonical Management resources and provider-neutral ports live in `Management.Abstractions`; validation and use cases live in `Management.Core`. SQLite and EF Core are confined to `Management.Storage.Sqlite`. Concrete `AIAgent` types are confined to `Runtime.AgentFramework`. Foundry is absent from every central project.

## Module responsibilities

| Module | Current responsibility | Planned extension |
|---|---|---|
| Management plane | canonical declarative agent and model-profile resources, typed references, desired state, generations, provisioning status, lifecycle events, deterministic revisions, deployments, ETag API | operations, policies, connections, identities, manifest import |
| Control storage | SQLite JSON resources with indexed metadata and optimistic concurrency | richer relational projections and migrations |
| Runtime plane | durable Run resources and events, SSE observation, cancellation/retry, MAF `ChatClientAgent`, in-process/shared-host provisioning, registry, reconciliation | provider-native token/tool streaming, sessions, dedicated hosts, containers, remote and Foundry adapters |
| Model providers | SQLite-backed provider declarations with ETag CRUD and usage protection, dynamic AEP health/model discovery, persisted logical profiles, and provider-neutral `IChatClient` resolution | credentials/connections, additional AEP extensions, cached discovery |
| Work plane | `WorkItem` lifecycle, interactions, idempotent runtime events, results, canonical REST API | durable dispatch, retry/recovery, requester authorization, artifact storage |
| Work storage | independent SQLite snapshots, indexed query fields, optimistic version concurrency | migrations and richer projections |
| Flows | typed graph drafts, validation, YAML/JSON source, immutable versions, durable sequential Runs, visual designer and SignalR replay | checkpoints, parallel and long-running steps |
| Flow storage | independent readable JSON documents, ETags, active/current definition separation | migrations, indirect reference projections |
| Identity | local-user boundary only | OIDC, users, members, workspace authorization |
| Workspaces | workspace and inbox lifecycle | teams, organizations, policies |
| Ingestion | text, JSON, multipart file, URL, hash deduplication | webhooks, email, connectors |
| Memory | normalized content, summaries, categories, search contract | facts, relations, embeddings, conversations |
| Routing | deterministic stateless decision | rule catalog and LLM router |
| Agents | management definitions plus isolated MAF runtime adapter | sessions, execution budgets, richer tool policies |
| Workflows | normalize → analyze → remember | parallel, routing, handoff, supervisor, HITL |
| Scheduling | standalone polling worker | Quartz persistent scheduler |
| Tools | persisted ToolProvider/Tool resources, AEP contribution resolution, MCP schema catalog and MAF invocation | richer permissions, credentials and connection policies |
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
Flow Run -> resolves exact published FlowReference -> sequential local execution
```

The Flow module is physically independent and owns editable typed graph drafts, immutable published snapshots, constrained expressions, and the provider-neutral Flow Run model. The local executor traverses `Input`, `Agent`, `Router`, `Condition`, `Transform`, `Output`, and `Failure` steps sequentially without referencing Microsoft Agent Framework; Infrastructure adapts agent steps and Management resource lookups.

### Flow Run vertical

```text
Console / API / future Work adapter
  -> POST published Flow Run returns 202 Accepted
  -> bounded local Flow queue
  -> validate input and persist the exact draft or published definition snapshot
  -> traverse typed steps and execute selected managed Agents through the Flow agent port
  -> persist differential events, transitions, diagnostics, usage, and failures
  -> SignalR updates with persisted replay, cancellation, global and per-Flow history
```

Flow Runs and direct agent Runtime Runs remain distinct resources and stores. A Flow Run may use an agent internally, while its public trace and lifecycle stay owned by Flow.

### Execution identities and ownership

The execution identifiers are intentionally not interchangeable:

| Identity | Owner | Lifetime | Relationship |
|---|---|---|---|
| `InteractionId` | Work | Durable user conversation | May create an initial task and later continuation executions. |
| `WorkTaskId` | Work | Durable functional task | Is the public identity of the anchor `WorkItem`; retries/continuations remain attached to it. |
| `FlowRunId` | Flow | One technical graph traversal | May be correlated to a Work task, but Flow owns its status, events, and trace. |
| Runtime Run ID | Runtime | One exact agent invocation | A direct Console/API invocation creates only this Run; a Flow may create one internally. |

Correlation never transfers ownership. Work stores functional history and results, Flow stores orchestration history, and Runtime stores agent-invocation telemetry. A retry creates a new technical Run while preserving the functional task or conversation correlation when one exists.

### Declarative agent vertical

```text
PUT canonical Agentstration.Agents/agents resource
  -> validate route/body identity, schema, API version, and typed resource references
  -> compare only desired state with the stored declaration
  -> preserve generation and ETag when identical, otherwise increment generation
  -> persist through the generic SQLite control-plane store
  -> publish AgentCreated or AgentUpdated (DELETE publishes AgentDeleted)
Console save-and-apply / explicit Runtime reconcile
  -> create or reuse the immutable revision and deployment for the current generation
  -> provision and observe the replacement Runtime instance
  -> when Ready, stop and deprovision every superseded deployment for the agent
  -> on failure, keep the previous healthy generation running
```

Management never constructs an `AIAgent`, resolves credentials, injects a model client, instantiates tools, or executes an agent. `ResolvedAgentSpec` is the provider-neutral boundary for the direct Agent definition, model profile, and tools; concrete MAF materialization remains in `Agentstration.Runtime.AgentFramework`.

Local activation is idempotent. During the short overlap needed for a safe replacement, routing selects the highest ready `AgentVersion` for each logical agent, so an older ready deployment cannot win because of storage enumeration order.

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

### AEP model-provider flow

```text
Local Ollama installation <--native HTTP-- autonomous Agentstration.Extensions.Ollama
AppHost --configures Ollama endpoint--> Agentstration.Extensions.Ollama
   |--injects AEP extension endpoint--> Agentstration hosts
Agent modelProfile.resourceId
   -> persisted Management profile
   -> projected runtime deployment
   -> persisted Management provider (AEP URL + contribution id + options)
   -> generic AEP model provider
   -> AepChatClient : Microsoft.Extensions.AI.IChatClient
   -> Runtime.AgentFramework
   -> MAF AIAgent
   -> AEP HTTP/JSON or SSE
   -> Agentstration.Extensions.Ollama
   -> OllamaSharp -> Ollama
```

The normal Web and Aspire composition uses the managed profile resolver: the persisted Model Profile and Model Provider are authoritative for contribution, AEP endpoint, and model selection. `Deterministic` remains the explicit offline/test mode. Startup seeds `ollama-local` only when absent: Aspire starts the AEP extension and configures it to use the existing local Ollama endpoint from `Ollama:Endpoint`, defaulting to `http://localhost:11434`; it does not provision Ollama or pull models. Direct launch of the extension uses the same default. Subsequent AEP URL changes take effect on the next resolution. Only `Agentstration.Extensions.Ollama` knows OllamaSharp or the native Ollama API. The profile resource ID remains in the immutable agent revision; no provider endpoint is embedded in an agent.

### AEP tool contribution and MCP flow

```text
Agent tool reference: Agentstration.Tools/tools/{name}
  -> persisted Tool resource (enablement, availability, schema, provider reference)
  -> persisted ToolProvider (AEP or MCP; provider enablement and connection)
  -> AEP descriptor mapping OR direct MCP tools/list
  -> MCP tools/list supplies input/output schemas and annotations
  -> official McpClientTool (Microsoft.Extensions.AI AITool)
  -> Runtime.AgentFramework -> MAF agent tool invocation -> MCP tools/call
```

AEP owns extension identity, presentation metadata, server declarations, and the mapping from a lightweight contribution to MCP. It deliberately carries no tool schema, invocation payload, result, or operational MCP error. MCP remains authoritative for `tools/list`, schema/annotations, `tools/call`, results, and protocol failures. Agentstration owns persistent `ToolProviderResource` and `ToolResource` documents, discovery state, assignment by canonical resource ID, enablement, and future policy. Direct external MCP is a ToolProvider and does not pass through AEP. The catalog is independent of MAF; the Runtime adapter consumes its provider-neutral `IAgentTool` and reuses the official SDK's native `AITool` when available.

Discovery is performed on provider create/update and by an explicit refresh operation. It materializes new tools as disabled, updates provider-owned metadata while preserving administrator enablement, marks disappeared tools unavailable without deleting them, and restores availability if they reappear. Runtime usability requires provider enabled, tool enabled, tool available, and an Agent assignment.

Extension endpoints are resolved from `Agentstration:Extensions:{extensionId}:Endpoint`. Relative MCP endpoints in AEP discovery are resolved against that extension base URL; absolute endpoints must use HTTP(S). The earlier AEP chat `AepToolDefinition`, `AepToolCall`, and `AepToolResult` contracts describe model-provider function-calling exchange only and are not an operational extension-tool protocol.

Model profiles and providers are persisted as `Agentstration.Models/modelProfiles` and `Agentstration.ModelProviders/modelProviders` documents in the Management control plane. The internal deployment configuration used by the runtime resolver is projected from the stored profile; it is not a separate public resource. Provider connectivity and discovered models are dynamic views and are not persisted. Provider writes validate adapter type, endpoint shape, and native options without requiring connectivity. Provider deletion queries exact profile references and fails while usages remain; profile deletion applies the equivalent rule to agent references.

The Interactive Server console consumes these same HTTP contracts through dedicated model-management clients. `/modelproviders` manages provider declarations and presents connectivity, dynamic discovery, and profile usages. `/modelprofiles` manages canonical inference resources (`generation`, `reasoning`, `output`, and provider-keyed options) with ETags and usage protection. `/runtimeprofiles` independently manages session, tool invocation, streaming defaults, and runtime-keyed options; deployments must reference an existing canonical runtime-profile resource ID. The reusable agent picker emits only the canonical model-profile resource ID, while agent details query `/api/agents/{name}/model` to keep declared and resolved configuration visually and structurally distinct.

Model behavior and runtime behavior are now separate canonical categories. `ModelProfileResource` carries `generation`, `reasoning`, `output`, and provider-keyed `providerOptions`; `RuntimeProfileResource` carries session/tool/streaming defaults and runtime-keyed `runtimeOptions`. `AgentDeployment` records the resolved agent and model-profile references alongside the runtime-profile reference. Runtime option layers are merged by category from provider/model defaults through profile, agent, runtime, Work/Flow, and explicit execution override, then validated as one effective configuration.

Runtime adapters expose normalized `AgentExecutionEvent` values rather than MAF updates. Effective capability resolution intersects provider, selected model, runtime, and concrete adapter support and preserves `Unsupported`, `Native`, `Emulated`, or `Partial`. The MAF adapter maps canonical options to `ChatOptions`; the Ollama adapter alone parses `think`, `keepAlive`, engine sizing, `endpointMode`, and its forward-compatible additional options. See ADR-0017.

Agent CRUD and Agent Runner always use canonical Management and Runtime HTTP clients, independently of simulated dashboard projections. This prevents a simulated agent generation from being activated against a different persisted generation. Before enabling Run the console combines `/api/agents/{name}/model` with `/api/runtime/agents/{name}/readiness`. Save-and-apply or **Reconcile runtime** calls `/prepare`, which creates or reuses the current revision and local deployment and reconciles it. At execution time the MAF adapter resolves the current profile again, merges profile defaults with the only supported overrides (`temperature`, `maxOutputTokens`), selects the deployment model through `ChatOptions.ModelId`, and records the actual provider/model/effective options on the durable Run.

### URL security flow

The reader only accepts absolute HTTP(S), rejects credentials, loopback, private/link-local/multicast addresses after DNS resolution, uses a 15-second timeout, streams headers first, and enforces a 2 MiB limit. Redirect revalidation is a planned hardening item; public deployment should disable automatic redirects until each hop is checked.

## Observability

Activity sources exist for ingestion, workflows, missions, Runtime Runs, Microsoft Agent Framework agents, and resolved model chat clients. A Runtime Run span carries the run, agent, generation, deployment, origin, and model-profile correlation identifiers. Its MAF `invoke_agent` span contains the provider-neutral agent execution, and its GenAI child span represents the effective request made through `IChatClient`; the existing `HttpClient` instrumentation remains the network-level child span.

MAF and model-client telemetry follows the OpenTelemetry GenAI conventions and is enabled by `Observability:GenAI:Enabled`, which defaults to `true`. OpenTelemetry sensitive-data capture is explicitly disabled in code: raw documents, prompts, responses, tool arguments, tool results, credentials, and authorization headers are not emitted by the normal telemetry pipeline. Operational logs use scopes carrying the Run and agent identifiers and export through OTLP alongside traces and metrics when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Aspire supplies the local dashboard endpoint.

`Observability:GenAI:HttpPayloadCapture` remains a separate, Development-only diagnostic boundary for the legacy OpenAI-compatible model pipeline. AEP requests and the out-of-process Ollama extension do not enable payload capture by default. Normal AEP `HttpClient` instrumentation records network telemetry without prompts, responses, tool arguments, credentials, or authorization headers.

## Identity, tenancy, and local bootstrap

The Management boundary persists a `Tenant -> Workspace` hierarchy and a global `User -> TenantMembership -> RoleAssignment -> RoleDefinition` authorization model in the SQLite control-plane database. Management resource rows carry explicit tenant and workspace scope columns in addition to their JSON payload. Store reads, writes, lists, and deletes are filtered by the initialized request context.

Standalone startup creates or repairs `local / default`, the local user, active tenant membership, and a tenant-level Owner assignment before management demo data is seeded. Parent tenant assignments inherit into every accessible workspace. The request pipeline validates the workspace selection stored in an HTTP-only cookie, installs it as an ambient request context, and restores the standalone fallback after the request. The Console exposes a dynamic workspace selector plus General, Workspaces, Members, and Access Control views. Management HTTP routes use `/api/...`; workspace scope comes from the authorized context. See ADR-0031.

## Implementation plan

1. **Delivered foundation:** solution conventions, domain/application boundaries, local content store, API/UI/MCP, OTel, Aspire, and tests.
2. **Delivered management vertical:** direct agent definitions, deterministic compilation, immutable revisions, SQLite control-plane storage, deployments, ETags, concise REST API, and pagination.
3. **Delivered runtime vertical:** isolated Microsoft Agent Framework adapter, in-process/shared-host provisioners, runtime registry, periodic reconciliation, single-agent routing, execution, and standalone sample data.
4. **Delivered content and monitoring verticals:** ingestion, memory/search, deterministic/OpenAI-compatible AI, missions, change detection, and internal notifications.
5. **Delivered Work vertical:** domain-controlled lifecycle, typed identifiers, interactions, idempotent Runtime events, independent SQLite persistence, local execution gateway, canonical REST API, metrics, traces, and tests.
6. **Delivered Flow authoring vertical:** independent projects, typed seven-step graphs, draft revisions and ETags, structural/resource/expression validation, YAML/JSON source, immutable publication, visual authoring, Work references, OpenAPI, and SQLite.
7. **Delivered Flow Runtime vertical:** durable FlowRun contracts and event history, immutable draft/published snapshots, bounded sequential typed-graph execution, input validation, cancellation, SignalR replay, telemetry, and the Flow-centered console.
8. **Next Work increment:** durable execution dispatch/recovery, requester authorization, external artifact storage, cancel propagation, and retry/relaunch operations.
9. **Delivered Runtime Run increment:** durable Run resources, local queue, exact agent-generation resolution, SQLite history, SSE observation, cancellation, retry, and Agent Runner console.
10. **Next management increment:** durable long-running operations, manifest importer, model/tool/connection/identity providers, and management authentication.
11. **Next runtime increment:** provider-native streaming and tool telemetry, session storage, tool catalog policies, revision traffic splitting, dedicated process/container and remote endpoint adapters.
12. **Delivered local model-provider increment:** provider-neutral resolver, the former in-process OllamaSharp adapter, Aspire-provisioned Ollama/model volume, Runner integration, development diagnostic, and offline tests; its adapter placement is superseded by AEP V1.
13. **Delivered declared model resolution increment:** agent profile reference to profile/deployment/provider resolution, async `IChatClient` resolution, MAF materialization, resolved model Run metadata, and boundary tests.
14. **Delivered model management API increment:** provider and profile CRUD with ETags, dynamic discovery/status/models, filters, usages, resolution, agent model expansion, Problem Details, and deletion protection.
15. **Delivered model management UI increment:** provider and profile CRUD, connection testing, dynamic model inspection, ETag conflict recovery, usage-aware deletion, reusable agent profile picker, and declared-versus-resolved agent model details.
16. **Delivered real Agent Runner invocation increment:** canonical Runner clients, exact-generation readiness/preparation, per-run profile resolution, dynamic Ollama model selection, effective generation options, and durable resolved-model metadata.
17. **Delivered AEP V1 increment:** technology-neutral protocol contracts, reusable client/server framework, Microsoft.Extensions.AI adapter, out-of-process Ollama extension, Aspire orchestration, SSE streaming, discovery/version checks, and offline boundary tests.
18. **Delivered AEP Tool Contributions increment:** schema-free AEP mappings to one or more MCP servers, persisted ToolProvider/Tool resources, an official-SDK Tool Catalog, direct external MCP support, and native MAF tool adaptation.
19. **Delivered Tool Provider governance increment:** persistent AEP/MCP providers, STDIO and Streamable HTTP, manual discovery with durable diffs, secure-default Tool materialization, Console governance, Agent selection, and deterministic AEP utilities.

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
- ADR-0014: configuration-backed model resolution into MAF
- ADR-0015: persisted model profiles and read-only provider APIs
- ADR-0016: real model invocation from Agent Runner
- ADR-0017: canonical runtime, model options, and effective capabilities
- ADR-0018: persisted model-provider declarations and dynamic clients
- ADR-0019: Flow-owned Run resource and execution console
- ADR-0020: Workplace Entry, Interaction, and Task vertical
- ADR-0021: standalone Workplace and Work API hosts
- ADR-0022: Interaction as durable conversation and FlowRun continuation
- ADR-0023: Console supervision of WorkTasks through Work API
- ADR-0024: Entries always target executable Flows
- ADR-0026: out-of-process model-provider extensions through AEP
- ADR-0027: AEP tool contributions resolve to MCP
- ADR-0028: Tool Providers materialize a governed catalog
- ADR-0029: Aspire consumes an existing local Ollama installation
- ADR-0030: AEP is an autonomous SDK and Inspector repository
- ADR-0031: Agentstration-native declarative resource envelope
- ADR-0032: one authoritative standalone server
- ADR-0033: canonical names and explicit execution identities
