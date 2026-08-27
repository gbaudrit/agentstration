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
  Agentstration.Flow/             provider-neutral Flow definitions and references
  Agentstration.Flow.Application/ Flow CRUD, publication, activation, resolution
  Agentstration.Flow.Contracts/   public Flow API contracts
  Agentstration.Flow.Storage.Abstractions/
  Agentstration.Flow.Storage.Sqlite/
  Agentstration.Infrastructure/   JSON/EF storage, AI, HTTP, event bus, queues
  Agentstration.Management.Abstractions/ canonical resources, ports, events, resolved specs
  Agentstration.Management.Core/  Management validation, use cases, revisions, deployments
  Agentstration.Management.Contracts/
  Agentstration.Management.Storage.Sqlite/
  ../aep/                            autonomous future AEP repository subtree
  Agentstration.Extensions.Ollama/   autonomous AEP-to-Ollama service
  Agentstration.Extensions.LlamaCpp/ autonomous AEP-to-llama.cpp service
  Agentstration.Extensions.LocalAI/  autonomous AEP-to-LocalAI service
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
  Agentstration.Management.Tests/
  Agentstration.Web.Tests/
  Agentstration.Web.Components.Tests/
  Agentstration.Web.FlowDesigner.Tests/
docs/decisions/
```

Core dependency direction:

```text
Web ---> Infrastructure ---> Application ---> Work
Web ---> Management / Flow / Runtime public boundaries

Web -> Management contracts + core
Management.Core -> Management.Abstractions + Runtime.Abstractions
Management.Contracts -> Management.Abstractions
Management.Storage.Sqlite -> Management.Abstractions + EF Core SQLite
Infrastructure -> SQLite control-plane storage + local/MAF runtime adapters
Web -> ModelProviders -> Aep.MicrosoftExtensionsAI -> Aep.Client
Extensions.Ollama -> Aep.AspNetCore + OllamaSharp
Extensions.LlamaCpp -> Aep.AspNetCore + native HTTP
Extensions.LocalAI -> Aep.AspNetCore + native HTTP
AppHost -> provider extensions (configured local inference endpoints)
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

Canonical Management resources and provider-neutral ports live in `Management.Abstractions`; validation and use cases live in `Management.Core`. SQLite and EF Core are confined to module-specific storage projects. Concrete `AIAgent` types are confined to `Runtime.AgentFramework`. Foundry is absent from every central project.

`Agentstration.Resources` contains the neutral namespace/address value types shared by Management, Flow, Work, and Runtime boundaries. Resource identity is `(workspace, namespace, kind, name)` and existing callers implicitly use `default`. Relative references inherit their owner's namespace; explicit cross-namespace references retain the supplied namespace. See ADR-0035.

## Module responsibilities

| Module | Current responsibility | Planned extension |
|---|---|---|
| Management plane | canonical declarative agent and model-profile resources, typed references, desired state, generations, provisioning status, lifecycle events, deterministic revisions, deployments, ETag API | operations, policies, connections, identities, manifest import |
| Pack distribution | local ZIP importer, retained source artifacts, Pack Projects, workspace-resource Composer with dependency closure, deterministic builds, direct current-Workspace installation, logical Model Profile/Model Provider/Runtime Profile/Secret bindings retained by Pack identity, coordinated six-kind lifecycle, provenance, compensation, and modification-safe uninstall | broader contained-resource authoring, fully scoped cross-Workspace install, dependency resolution, signatures, Gallery, and publisher verification |
| Control storage | SQLite JSON resources with indexed metadata and optimistic concurrency | richer relational projections and migrations |
| Runtime plane | durable Run resources and events, SSE observation, cancellation/retry, MAF `ChatClientAgent`, in-process/shared-host provisioning, registry, reconciliation | provider-native token/tool streaming, sessions, dedicated hosts, containers, remote and Foundry adapters |
| Model providers | SQLite-backed extension registrations and provider bindings with ETag CRUD and usage protection, explicit configuration/Aspire refresh, dynamic AEP health/model discovery, persisted logical profiles, and provider-neutral `IChatClient` resolution | additional AEP extensions, cached discovery |
| Work plane | `WorkItem` lifecycle, interactions, idempotent runtime events, results, canonical REST API | durable dispatch, retry/recovery, requester authorization, artifact storage |
| Work storage | independent SQLite snapshots, indexed query fields, optimistic version concurrency | migrations and richer projections |
| Flows | typed graph drafts, typed orchestration authoring, immutable versions, durable Runs, opaque SQLite-backed runtime state, interactive input suspension/recovery, visual editors and SignalR replay | distributed dispatch, arbitrary graph waits, HumanApproval nodes, and provider-level effect idempotency |
| Flow storage | independent readable JSON documents, ETags, active/current definition separation | migrations, indirect reference projections |
| Identity | local accounts, Principal mapping, Principal preferences, Workspace memberships/RBAC, bootstrap, account security, append-only security audit | external-account provisioning/linking, recovery, workload authentication |
| Routing | deterministic stateless decision | rule catalog and LLM router |
| Agents | management definitions plus isolated MAF runtime adapter | sessions, execution budgets, richer tool policies |
| Workflows | normalize → analyze → remember | parallel, routing, handoff, supervisor, HITL |
| Scheduling | Workspace-scoped Trigger resources, durable occurrences, Quartz.NET persistent SQLite projection, startup reconciliation, explicit misfire/concurrency policy and authorized Work submission | webhook/event/condition sources, workload identities, clustered scheduling |
| Tools | persisted ToolProvider/Tool resources, AEP contribution resolution, MCP schema catalog, and an Agentstration-owned runtime execution boundary before MCP `tools/call` | richer permissions, credentials, connection policies, and execution hooks |
| Notifications | Work and Workplace notification records | email, Teams, webhook channels |
| MCP | generic governed MCP provider/client infrastructure; no built-in legacy platform tools | managed server-side tools and authorization |
| Observability | OTel traces/metrics/log correlation tags | dashboards and SLOs |

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

public interface ITriggerSchedulerProjection { Task ReconcileAsync(TriggerResource trigger, CancellationToken cancellationToken); }
```

Other important contracts are owned by their Management, Flow, Runtime, Work, Tool, Pack and identity modules. Expected business failures use explicit module results or exceptions translated at the HTTP boundary.

## Initial data model

The executable model is split across canonical Management resources, Work Items and Workplace interactions, Flow definitions and runs, Runtime Runs, identity records, Packs, Triggers, Tools and module-owned notifications and artifacts.

Every workspace-owned record carries `WorkspaceId`. Queries require it alongside the entity identifier. Runtime runs, Flow definitions and runs, Work items, events, queues, cancellation state, and artifacts preserve that scope end to end; storage identities are composite where identifiers may repeat across workspaces. HTTP scope comes from the authenticated request context rather than caller-controlled payload or query values, and background workers re-authorize the durable scope before execution. See ADR-0050 and ADR-0053.

## Main flows

### Trigger vertical

```text
TriggerResource (Management desired state)
  -> Quartz SQLite projection (reconstructible)
  -> durable TriggerOccurrence (idempotency and pre-Work outcome)
  -> current Principal authorization
  -> exact immutable FlowReference
  -> WorkItem origin/correlation
  -> FlowRun
  -> Runtime
  -> autonomous Task/result or existing task-scoped PendingAction
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
Flow Run -> resolves exact published FlowReference -> local graph execution or isolated MAF orchestration adapter
```

The Flow module is physically independent and owns editable typed graph drafts, immutable published snapshots, constrained expressions, and the provider-neutral Flow Run model. The local executor traverses `Input`, `Agent`, `Router`, `Condition`, `Transform`, `Output`, and `Failure` steps sequentially without referencing Microsoft Agent Framework; Infrastructure adapts agent steps and Management resource lookups.

### Flow Run vertical

```text
Console / API / future Work adapter
  -> POST published Flow Run returns 202 Accepted
  -> bounded local Flow queue
  -> validate input and persist the exact draft or published definition snapshot
  -> traverse typed steps or execute a bounded provider-neutral orchestration through the runtime adapter
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
Local llama-server     <--native HTTP-- autonomous Agentstration.Extensions.LlamaCpp
LocalAI server         <--native HTTP-- autonomous Agentstration.Extensions.LocalAI
AppHost --configures native endpoints--> provider extensions
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
   -> selected AEP extension
      |-> Agentstration.Extensions.Ollama -> OllamaSharp -> Ollama
      |-> Agentstration.Extensions.LlamaCpp -> OpenAI-compatible/native HTTP -> llama-server
      `-> Agentstration.Extensions.LocalAI -> OpenAI-compatible/native HTTP -> LocalAI
```

The normal Web and Aspire composition uses the managed profile resolver. A persisted `ExtensionRegistration` is authoritative for an AEP endpoint, source, enabled state, expected identity, and transport credential. A Model Provider explicitly references that registration and selects one model-provider contribution; a Model Profile then selects the model and pins contribution-native option contracts. Runtime resolution separately selects the `aep` adapter and the contribution ID. `Deterministic` remains the explicit offline/test mode. Startup materializes configured and Aspire endpoints as stable read-only registrations and seeds independent `ollama-local`, `llama-cpp-local`, and `localai-local` bindings when absent. Aspire starts all three AEP extensions and configures them to use existing local inference servers; it provisions no server and downloads no model. LocalAI filters its heterogeneous catalog through `/v1/models/capabilities` and never forwards provider-owned MCP selection metadata. Subsequent registration URL changes take effect on the next resolution. Only the concrete extension knows its native API. The profile resource ID remains in the immutable agent revision; no extension endpoint is embedded in an agent or Model Provider. See ADR-0067.

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

AEP owns extension identity, presentation metadata, server declarations, and the mapping from a lightweight contribution to MCP. It deliberately carries no tool schema, invocation payload, result, or operational MCP error. MCP remains authoritative for `tools/list`, schema/annotations, `tools/call`, results, and protocol failures. Agentstration owns persistent `ToolProviderResource` and `ToolResource` documents, discovery state, assignment by canonical resource ID, enablement, and approval policy. Direct external MCP is a ToolProvider and does not pass through AEP. The catalog is independent of MAF; the Runtime adapter consumes its provider-neutral `IAgentTool` and reuses the official SDK's native `AITool` when available. A governed tool marked `requiresApproval` is exposed as an `ApprovalRequiredAIFunction`; MAF's external request then follows the durable `InputRequest` suspension and resume path.

Discovery is performed on provider create/update and by an explicit refresh operation. It materializes new tools as disabled, updates provider-owned metadata while preserving administrator enablement, marks disappeared tools unavailable without deleting them, and restores availability if they reappear. Runtime usability requires provider enabled, tool enabled, tool available, and an Agent assignment.

Extension endpoints are resolved from `Agentstration:Extensions:{extensionId}:Endpoint`, Aspire connection strings named `*-extension`, and workspace-owned `ExtensionRegistration` Management resources. The Extensions inventory treats enabled registrations as discovery candidates and does not scan the local network. Manual registrations have their own ETag-protected CRUD surface and can be disabled without being deleted. Relative MCP endpoints in AEP discovery are resolved against that extension base URL; absolute endpoints must use HTTP(S). The earlier AEP chat `AepToolDefinition`, `AepToolCall`, and `AepToolResult` contracts describe model-provider function-calling exchange only and are not an operational extension-tool protocol.

`POST /api/extensions/discover` is the explicit source-refresh command. It re-enumerates the current `Agentstration:Extensions` configuration and Aspire `ConnectionStrings`, validates HTTP(S) endpoints, and synchronizes stable read-only registrations. Its response reports sources, created, updated, and unchanged counts. It neither starts extension processes nor probes arbitrary addresses. The Console's **Discover extensions** action invokes this command, reloads `GET /api/extensions`, and displays both the synchronization counts and the number of AEP endpoints that were successfully inspected; a zero-source result is shown explicitly rather than treated as a silent refresh.

Option migrations remain explicit Management operations. Extensions publish directed migration edges and execute the semantic transformations; the AEP server validates every step, while Agentstration independently validates the returned target envelope. Preview never writes, and apply reruns the migration against current persisted values before an ETag-protected Model Profile update.

Model profiles and providers are persisted as `Agentstration.Models/modelProfiles` and `Agentstration.ModelProviders/modelProviders` documents in the Management control plane. The internal deployment configuration used by the runtime resolver is projected from the stored profile; it is not a separate public resource. Provider connectivity and discovered models are dynamic views and are not persisted. Provider writes validate adapter type, endpoint shape, and native options without requiring connectivity. Provider deletion queries exact profile references and fails while usages remain; profile deletion applies the equivalent rule to agent references.

The Interactive Server console consumes these same HTTP contracts through dedicated model-management clients. `/modelproviders` manages provider declarations and presents connectivity, dynamic discovery, and profile usages. `/modelprofiles` manages canonical inference resources (`generation`, `reasoning`, `output`, and provider-keyed options) with ETags and usage protection. Its provider-options editor is generated from the live extension's exact versioned schema: new values use the preferred contract, persisted values remain pinned, and unavailable or mismatched contracts fall back to explicit raw JSON editing. `/runtimeprofiles` independently manages session, tool invocation, streaming defaults, and runtime-keyed options; deployments must reference an existing canonical runtime-profile resource ID. The reusable agent picker emits only the canonical model-profile resource ID, while agent details query `/api/agents/{name}/model` to keep declared and resolved configuration visually and structurally distinct.

Model behavior and runtime behavior are now separate canonical categories. `ModelProfileResource` carries `generation`, `reasoning`, `output`, and provider-keyed `providerOptions`; each provider-native value pins an immutable AEP option-set version and schema digest. `RuntimeProfileResource` carries session/tool/streaming defaults and runtime-keyed `runtimeOptions`. `AgentDeployment` records the resolved agent and model-profile references alongside the runtime-profile reference. Runtime option layers are merged by category from provider/model defaults through profile, agent, runtime, Work/Flow, and explicit execution override, then validated as one effective configuration.

Runtime adapters expose normalized `AgentExecutionEvent` values rather than MAF updates. Resolution carries dynamically observed provider and selected-model capabilities with the client. Before invocation, effective capability resolution intersects provider, selected model, runtime, and concrete adapter support and preserves `Unsupported`, `Native`, `Emulated`, or `Partial`. The MAF adapter maps canonical options to `ChatOptions`; each AEP extension validates and maps only its own native options. See ADR-0017 and ADR-0061.

Agent CRUD and Agent Runner always use canonical Management and Runtime HTTP clients, independently of simulated dashboard projections. This prevents a simulated agent generation from being activated against a different persisted generation. Before enabling Run the console combines `/api/agents/{name}/model` with `/api/runtime/agents/{name}/readiness`. Save-and-apply or **Reconcile runtime** calls `/prepare`, which creates or reuses the current revision and local deployment and reconciles it. At execution time the MAF adapter resolves the current profile again, merges profile defaults with the only supported overrides (`temperature`, `maxOutputTokens`), selects the deployment model through `ChatOptions.ModelId`, and records the actual provider/model/effective options on the durable Run.

## Observability

Activity sources exist for Work, Flow Runs, Runtime Runs, Microsoft Agent Framework agents, and resolved model chat clients. A Runtime Run span carries the run, agent, generation, deployment, origin, and model-profile correlation identifiers. Its MAF `invoke_agent` span contains the provider-neutral agent execution, and its GenAI child span represents the effective request made through `IChatClient`; the existing `HttpClient` instrumentation remains the network-level child span.

MAF and model-client telemetry follows the OpenTelemetry GenAI conventions and is enabled by `Observability:GenAI:Enabled`, which defaults to `true`. OpenTelemetry sensitive-data capture is explicitly disabled in code: raw documents, prompts, responses, tool arguments, tool results, credentials, and authorization headers are not emitted by the normal telemetry pipeline. Operational logs use scopes carrying the Run and agent identifiers and export through OTLP alongside traces and metrics when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Aspire supplies the local dashboard endpoint.

`Observability:GenAI:HttpPayloadCapture` remains a separate, Development-only diagnostic boundary for the legacy OpenAI-compatible model pipeline. AEP requests and the out-of-process Ollama extension do not enable payload capture by default. Normal AEP `HttpClient` instrumentation records network telemetry without prompts, responses, tool arguments, credentials, or authorization headers.

## Identity, tenancy, and local bootstrap

The Management boundary persists a `Tenant -> Workspace` hierarchy and a global `User -> TenantMembership -> RoleAssignment -> RoleDefinition` authorization model in the SQLite control-plane database. Management resource rows carry explicit tenant and workspace scope columns in addition to their JSON payload. Store reads, writes, lists, and deletes are filtered by the initialized request context.

In explicit Development mode, standalone startup creates or repairs the development Principal and its `local / personal` context. `personal` is only the seeded Workspace name and has no capability semantics. In the default Local mode, a fresh instance instead exposes a one-time Web bootstrap that creates the first ASP.NET Core Identity account, its Principal, the initial tenant and workspace, Workspace Owner assignment, and Platform administrator grant. No default credential exists. The request pipeline resolves the authenticated identity, validates the workspace selection stored in an HTTP-only cookie, and installs it as an ambient request context for the request. A global fallback policy requires authentication; only health, bootstrap, authentication entry points, the corresponding Razor Pages, and their static assets are explicitly anonymous. HTTP APIs declare contextual RBAC policies, while MCP tool calls require `runs/execute` and both Workplace and Flow Run SignalR hubs require `runs/read`. Principal-scoped presentation preferences are stored separately from credentials and Workspace authorization; Console and Workplace currently share the persisted `System`, `Light`, or `Dark` theme selection. The Console exposes a dynamic workspace selector plus General, Workspaces, Members, Access Control, and PlatformAdmin-only Security audit views. Platform administration can be transferred explicitly to another active Principal; self-revocation, self-disable, and removal of the last active administrator are rejected. Platform administrators can also link exact OIDC `(Issuer, Subject)` pairs to existing human Principals without email matching or provider-specific types. Authentication and authorization mutations append structured identifier-only events to the Management Control Plane. Management HTTP routes use `/api/...`; workspace scope comes from the authorized context. See ADR-0042, ADR-0045, ADR-0046, ADR-0047, and ADR-0049.

SQLite schema evolution for the workspace-scope hardening increment is reset-only: existing generated databases are not altered or backfilled and must be deleted and reseeded. See ADR-0050.

## Implementation plan

1. **Delivered foundation:** solution conventions, explicit module boundaries, API/UI/MCP infrastructure, OTel, Aspire, and tests.
2. **Delivered management vertical:** direct agent definitions, deterministic compilation, immutable revisions, SQLite control-plane storage, deployments, ETags, concise REST API, and pagination.
3. **Delivered runtime vertical:** isolated Microsoft Agent Framework adapter, in-process/shared-host provisioners, runtime registry, periodic reconciliation, single-agent routing, execution, and standalone sample data.
4. **Retired legacy vertical:** the historical content ingestion, memory search and Mission monitoring stack was removed after the Management, Work, Flow, Runtime and Trigger modules superseded its responsibilities. See ADR-0071.
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
20. **Delivered Pack distribution increment:** Packs are versioned Management/distribution artifacts above ordinary resources, never execution primitives; local ZIP validation, namespace-scoped coordinated installation, provenance, inventory, compensation, and safe uninstall are executable offline.
21. **Delivered Pack authoring increment:** newly installed sources are content-addressed, installed Packs can be forked into workspace-owned Pack Projects, source and fork coexist in identity-derived namespaces, unchanged revisions build identical immutable archives, and stored builds can be previewed, downloaded, installed, or explicitly reinstalled in the current Workspace without a download/upload loop. Pack Flows preserve editable graph definitions.
22. **Delivered Pack bindings increment:** Pack manifests declare logical Model Profile, Model Provider, Runtime Profile, and Secret requirements; installation resolves them to namespaced workspace resources without copying Secret values, and selections persist by Pack identity across uninstall and reinstall. Automatic local deployment uses the Runtime Profile selected for a Pack Agent. See ADR-0062.
23. **Delivered Pack composition increment:** the Console catalogs current workspace resources, previews the complete Entry/Flow/Agent dependency closure, converts environment-specific Model Profile references into logical bindings, and creates a validated immutable Pack Project source snapshot without mutating the selected resources.
24. **Delivered durable interactive execution increment:** Flow Runs persist exact participant revision/deployment bindings and opaque runtime checkpoints, expose durable input requests through REST and Workplace pending actions, recover through at-least-once leases, expire unanswered requests, and protect live revisions with impact-aware normal and forced purge operations. See ADR-0054.
25. **Delivered governed Tool lifecycle projection:** the provider-neutral Tool execution pipeline emits started/completed/failed-or-cancelled facts. Runtime Runs project one `RuntimeToolCall` per logical call with physical attempt identity and count; Flow Runs append the same lifecycle to their durable journal. Arguments and results remain excluded from durable projections by default. See ADR-0055.
26. **Delivered local Tool execution hook chain:** locally registered provider-neutral guards execute in stable order before invocation, may allow or deny without mutating payloads, unwind terminal notifications in reverse order, and classify denial/hook/provider/cancellation outcomes. Every physical at-least-once attempt re-executes the chain. See ADR-0056.
27. **Delivered workspace-configurable Tool guard increment:** canonical `ToolExecutionHook` resources expose namespaced ETag CRUD and select built-in Runtime handlers by Tool, Tool Provider and Agent within the current Tenant/Workspace. The first bounded handler is `deny`; arbitrary code, scripts and remote hooks are not accepted. See ADR-0057.
28. **Delivered durable Tool governance trace:** every physical Tool attempt records the ordered hook identities, Management resource generations and allow/deny/failure decisions before provider invocation. Runtime and Flow journals retain per-attempt facts without arguments or results; failure to project the decision prevents the provider call. See ADR-0058.
29. **Delivered Tool governance audit read API:** `GET /api/tool-governance/{runtime|flow}/{runId}` reads the current Workspace's existing durable journal with an `afterSequence` cursor, bounded `limit`, and exact Tool call, physical invocation, Tool, Hook, HookResource generation and decision filters. The safe default response exposes invocation and policy identities without provider results or denial messages.
30. **Delivered Tool governance Console view:** Runtime and Flow Run details link to a Run-scoped audit page. Runtime links preserve the logical `ToolCallId` and physical `InvocationId`; operators can filter and paginate the evaluated Hook chain, resource generation, order, decision and stable code.
31. **Delivered opt-in Tool argument retention:** `Agentstration:ToolExecution:PersistArguments` defaults to `false`. Manual Runtime Runs expose an immutable tri-state override (`inherit`, `retain`, `do not retain`); retries preserve it. When effective, provider-neutral arguments are copied into the durable lifecycle projection, bounded by the host `MaximumArgumentsLength`, and shown on the Tool Governance view. Provider results remain excluded. See ADR-0059.
32. **Delivered Entry-driven Workplace presentation increment:** Entry configures participant, progress, Task, and Result presentation while Workplace composes existing durable Work primitives into one conversation timeline. Flow and Runtime remain presentation-neutral. See ADR-0060.

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
- ADR-0049: Workplace Dashboards own Entry composition
- ADR-0024: Entries always target executable Flows
- ADR-0026: out-of-process model-provider extensions through AEP
- ADR-0027: AEP tool contributions resolve to MCP
- ADR-0028: Tool Providers materialize a governed catalog
- ADR-0029: Aspire consumes an existing local Ollama installation
- ADR-0030: AEP is an autonomous SDK and Inspector repository
- ADR-0031: Agentstration-native declarative resource envelope
- ADR-0032: one authoritative standalone server
- ADR-0033: canonical names and explicit execution identities
- ADR-0034: MAF Flow orchestration behind the runtime adapter
- ADR-0035: explicit resource namespaces
- ADR-0036: runtime resolution and control-plane hardening
- ADR-0037: Packs are Management and distribution artifacts
- ADR-0038: Pack Projects retain sources and produce local immutable builds
- ADR-0042: authentication and authorization boundaries
- ADR-0043: trusted Console API session propagation
- ADR-0044: durable Identity schema and Data Protection key material
- ADR-0051: Pack Projects can originate from reviewed workspace snapshots
- ADR-0045: append-only Management security audit
- ADR-0046: transferable Platform administration
- ADR-0047: explicit external identity links
- ADR-0048: durable Flow Run execution scope
- ADR-0050: explicit background Control Plane access
- ADR-0052: Pack composition distinguishes contained model configuration from bindings
- ADR-0053: Workspace scope is part of durable identity
- ADR-0054: durable interactive Flow execution and exact runtime identity
- ADR-0055: Agentstration-owned Tool execution boundary
- ADR-0056: ordered Runtime guards for Tool execution
- ADR-0057: workspace-scoped Tool Hook resources select built-in Runtime handlers
- ADR-0058: Tool governance decisions are traced per physical attempt
- ADR-0059: Tool arguments require explicit bounded retention
- ADR-0060: Entry owns Workplace execution presentation
- ADR-0061: llama.cpp AEP provider and effective capability resolution
- ADR-0062: immutable versioned extension option contracts
