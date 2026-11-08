# Composition and service registration

The authoritative server composes explicit module registrations through `AddAgentstration` and named host extensions. [ADR-0189](../../decisions/0189-service-registration-has-explicit-module-ownership.md) records the ownership and selection rules. This map covers production registration sites affected by [#240](https://github.com/gbaudrit/agentstration/issues/240), including the Console and API projects extracted since the original issue.

Each row assigns the **complete registration body** of its methods, including aliases, options, framework builders, typed/named HTTP clients and helper-generated descriptors. Façades delegate ownership; dictionary `TryAdd` calls in business services are outside this DI inventory. Project/source paths below are relative to `src/Agentstration.`.

## Infrastructure pipeline

`Infrastructure/DependencyInjection.cs` retains both public `AddAgentstration` overloads. The options overload constructs validated shared composition state before adding descriptors, then invokes these internal extensions in order. Each lives in `Infrastructure/Composition/{Owner}ServiceCollectionExtensions.cs`.

| Owner/file prefix | Extension | Complete responsibility |
| --- | --- | --- |
| `Foundation` | `AddAgentstrationFoundation` | Clock, bootstrap fallback, shared request/trigger context and aliases, resource reference/scope services, GenAI fallback options/handler, Management events. |
| `Ai` | `AddAgentstrationAi` | AI options and mutually exclusive deterministic or OpenAI-compatible fallback client; Managed mode delegates resolution to Model Providers. |
| `Storage` | `AddAgentstrationStorage` | Storage selection/options, initialization coordinator, Workspace provisioning aliases, initializer and one provider's control, Resource Planning, Runtime, Work and Flow stores. |
| `StorageResources` | `AddStorageResources` (called by `AddAgentstrationStorage`) | Storage type/resource services, neutral resolver/content gateway, pure local definition validation, AEP type provisioning, protected invocation, local backend and idempotent defaults. Artifact composition registers the exact-receipt usage provider and durable adapter alias. |
| `Security` | `AddAgentstrationSecurity` | Master key/Vault providers, resource identity/access policies, Parameters, Secrets/capabilities, Principal/topology provisioning, authorization, administrator/external-identity lifecycle, auditing, membership/preferences/PAT services. |
| `Bootstrap` | `AddAgentstrationBootstrapHandlers` | Scoped declarative resource handlers, including StorageType and Storage; local-account/administrator handling stays in Identity. |
| `ResourcePlanning` | `AddAgentstrationResourcePlanning` | Plan validation/materialization/options/state reader, Plan/ChangeSet services and canonical validation/application adapters. |
| `Agents` | `AddAgentstrationAgents` | Compilation, runtime resolution/access, deferred profile-validator and single-client resolver defaults, deployment reconciliation, routing, retention and execution/preparation aliases. |
| `Packs` | `AddAgentstrationPacks` | Archive/artifact stores, all resource handlers, workspace catalog, management, authoring, composition and Source-catalog Pack integration. |
| `Sources` | `AddAgentstrationSources` | Scope/manifest/verification, bounded HTTP clients, snapshot/cache stores, Channel materialization/refresh, catalog readers/resolution, Registry management/trust/discovery. |
| `Schemas` | `AddAgentstrationSchemas` | Built-in and AEP format contributions/options, registry, management and binding adapter. |
| `Tools` | `AddAgentstrationTools` | MCP module, Tool/ToolSet/category/definition/hook services, enrollment provisioners, artifact guard, capture default, Runtime/Flow audit sinks, governance/pipeline and Flow Tool adapters. |
| `TriggerScheduling` | `AddAgentstrationTriggerScheduling` | Schedule/context/target/authorization/Work adapters, management/firing, Quartz persistence and ordered optional scheduler hosting. |
| `RuntimeRuns` | `AddAgentstrationRuntimeRuns` | AWP queue/scope, Run state/lifecycle, Worker lease/dispatch options/signals/guards, assignment projection, execution material/operation gateways and observability. |
| `Artifacts` | `AddAgentstrationArtifacts` | Artifact filesystems, staging executor, management/materialization, built-in provisioners, cleanup options and optional worker. |
| `WorkPlane` | `AddAgentstrationWorkPlane` | Execution gateway/queue aliases, Work/Workplace lifecycle/projections/discovery/operations, Task deletion, Entries/Dashboards, concrete Entry deletion guard and its resource-guard alias, and Flow input projection/response. |
| `InternalTools` | `AddAgentstrationInternalTools` | Notification Tool, documentation catalog, all explicit diagnostics/time/planning/artifact/acquisition/retrieval Tool contributions, projection and lazy collection. `AddInternalTool<TTool>` owns each concrete Tool and its definition/handler aliases. |
| `DataAndKnowledge` | `AddAgentstrationDataAndKnowledge` | Data Source profiles/discovery/targets/acquisition, HTTP fetcher, Knowledge management/projection/retrieval/Snapshots/exposure, Flow/artifact adapters, AEP bundle installer and optional recovery worker. |
| `FlowPlane` | `AddAgentstrationFlowPlane` | Scope/discovery/Agent/Tool resolution, guards/retention, Run queue/cancellation/scope/accessor, root submission, default sink, expression parser aliases, validation/artifact capture/drafts/Runs. |

`AgentstrationCompositionOptions` groups the data directory, AI/storage choices, optional per-plane SQLite connection strings, hosting toggle, Source verification/transport limits and Worker lease/dispatch options. The compatibility overload maps its existing arguments to this object. `AgentstrationCompositionContext` validates and selects storage before mutation; registration performs no initialization, bootstrap or discovery.

## Module-owned extensions

The following extensions retain their complete registration bodies. Calling them does not transfer ownership to Infrastructure or a host.

| Project/source | Owner extension | Registration scope |
| --- | --- | --- |
| `Models.Application/ModelManagementServiceCollectionExtensions.cs` | `AddAgentstrationModelManagement` | Provider management/discovery/configuration, Parameter usage contribution, Profile management/migration/stores and authoritative validator. |
| `ModelProviders/ModelProviderServiceCollectionExtensions.cs` | `AddAgentstrationModelProviders` | Configured AEP security/GenAI options, capture handler, HTTP provider transport/aliases, Source materialization, provider resolver and optional Managed profile resolver. |
| `Tools.Mcp/McpToolCatalog.cs` | `AddAgentstrationMcpTools` | Configuration/security defaults, three named HTTP clients, extension/environment resolution, transport, catalog/discovery/runner/invoker. |
| `Security.AspNetCoreIdentity/LocalIdentityDatabaseServices.cs` | `AddAgentstrationLocalIdentity` → `AddAgentstrationIdentity` | SQLite selection, EF Identity context/stores, claims factory, bootstrap/account/security/authentication services, lock/initializer and Data Protection persistence. |
| `Security.AspNetCoreIdentity.PostgreSql/PostgreSqlIdentity.cs` | `AddAgentstrationPostgreSqlIdentity` → `AddAgentstrationIdentity` | PostgreSQL context/migrations selection and the shared Identity registration. |
| `ResourceManagement.Storage.Sqlite/SqliteResourceStore.cs` | `AddSqliteResourceManagement` | Context/governance defaults, EF factory, resource/query/scope/policy/Trigger stores and Identity/audit/PAT aliases. `AddSqliteControlPlane` delegates for compatibility. |
| `ResourceManagement.Storage.PostgreSql/PostgreSqlResourceStore.cs` | `AddPostgreSqlResourceManagement` | Equivalent PostgreSQL contracts. `AddPostgreSqlControlPlane` delegates for compatibility. |
| `ResourcePlanning.Storage.Sqlite/SqliteResourcePlanRepository.cs` | `AddSqliteResourcePlanning` | EF factory and Plan/ChangeSet persistence. |
| `ResourcePlanning.Storage.PostgreSql/PostgreSqlResourcePlanRepository.cs` | `AddPostgreSqlResourcePlanning` | PostgreSQL EF factory and Plan/ChangeSet persistence. |
| `Runtime.Storage.Sqlite/SqliteRuntimeRunStore.cs` | `AddSqliteRuntimeRuns` | Runtime EF factory and Run/event/assignment persistence. |
| `Runtime.Storage.PostgreSql/PostgreSqlRuntimeRunStore.cs` | `AddPostgreSqlRuntimeRuns` | Equivalent PostgreSQL Runtime persistence. |
| `Work.Storage.Sqlite/WorkStorageSchema.cs` | `AddSqliteWorkPlane` | Work EF factory and Work/Interaction/Task/Entry/Dashboard/notification persistence. |
| `Work.Storage.PostgreSql/PostgreSqlWorkItemRepository.cs` | `AddPostgreSqlWorkPlane` | Equivalent PostgreSQL Work persistence. |
| `Flows.Storage.Sqlite/SqliteFlowRepository.cs` | `AddSqliteFlowStorage` | Flow EF factory and definition/Run persistence. |
| `Flows.Storage.PostgreSql/PostgreSqlFlowRepository.cs` | `AddPostgreSqlFlowStorage` | Equivalent PostgreSQL Flow persistence. |
| `Web.Components/ServiceCollectionExtensions.cs` | `AddAgentstrationWebComponents` | Circuit state/navigation, clock and empty context/search/status/palette/conversation/preferences defaults. |
| `Web.Components/Localization/AgentstrationLocalization.cs` | `AddAgentstrationLocalization` | RESX localization and validated localization/request-culture options. |
| `Web.FlowDesigner/DependencyInjection/FlowDesignerServiceCollectionExtensions.cs` | `AddAgentstrationFlowDesigner` | Scoped editor state; backend/resource adapters stay in their hosts. |
| `Workplace.Client/WorkplaceApiClient.cs` | `AddAgentstrationWorkplaceClient` | Typed Workplace API and reconnecting realtime client. |

SQLite and PostgreSQL rows are alternatives. The standalone host selects local-account Identity with the same provider, and Quartz uses that provider's scheduler store. Existing lifetimes and separate persistence boundaries remain unchanged.

## Authoritative host and transport

`Web/Program.cs` creates, composes, builds, configures, initializes and runs. `Web/Hosting/StandaloneHostComposition.cs` reads the standalone profile/configuration and paths, delegates registration, and keeps endpoint mapping and ordered startup explicit. `Web/Hosting/StandaloneHostServiceCollectionExtensions.cs` owns:

| Owner extension | Complete registration scope |
| --- | --- |
| `AddAgentstrationBootstrapHosting` | Configured bootstrap/capture options and scoped declarative bootstrap service. |
| `AddAgentstrationExtensionHosting` | Source Provider/Extension management/inventory, AEP enrollment/settings/policy/discovery/provisioning, AWP identity, standard Runtime Profile seeder and scope inventory. |
| `AddAgentstrationServerPresentation` | API façade, optional Razor/interactive rendering and localization. |
| `AddAgentstrationHostIdentity` | Selected SQLite/PostgreSQL local-account Identity. |
| `AddAgentstrationBackgroundWorkers` | Optional Work execution, assignment lease reaper, Flow recovery and Source refresh workers. |
| `AddAgentstrationTestingCleanup` | Private test-directory cleanup with its selected SQLite connection strings. |
| `AddAgentstrationHostObservability` | Logging/tracing/metrics and optional OTLP with existing source/meter configuration. |

`Api/Configuration/ApiTransportServiceCollectionExtensions.cs` preserves `AddAgentstrationApi`, owns SignalR/Problem Details, and delegates to the existing family `Add*Api` extensions and these focused methods:

| Owner/source | Complete registration scope |
| --- | --- |
| `AddAgentstrationBootstrapTransport` in API composition | Bootstrap catalog, Source loader, lock, profile management and API alias. |
| `AddAgentstrationConsoleResourceQueries` in API composition | Source Console query service/alias and Resource scope API service. |
| `AddAgentstrationRealtimeProjection` in API composition | Workplace Flow conversation projection and explicit composite Flow sink selection. |
| `AddIdentityApi` in `Identity.Api/IdentityApiModule.cs` | Façade over `AddAgentstrationWorkloadTrust` (BFF/AWP/delegation options, keys/caches/services and trust-audit worker), `AddAgentstrationEnrollmentRateLimiting`, `AddAgentstrationApiAuthentication` (scheme selection/token validation), and `AddAgentstrationApiAuthorization` (handlers/policies). |
| `AddFlowsApi` in `Flows.Api/FlowsApiModule.cs` | Concrete SignalR Flow Run sink consumed by the composite. |
| `AddWorkplaceApi` in `Workplace.Api/WorkplaceApiModule.cs` | SignalR Workplace event sink. |
| `AddAgentstrationOpenApi` in `Api/Configuration/OpenApiConfiguration.cs` | OpenAPI document/security/schema transformers. |

Other family API extensions aggregate owned endpoints without relocating application registrations. Shared API authentication/authorization also serves the embedded Console; scheme selection retains its configured order.

## Console hosts

`Web/Configuration/WebConsoleServiceCollectionExtensions.cs` preserves `AddAgentstrationWebConsole`. Its façade owns validated API options and the HTTP context accessor, then delegates:

| Embedded Console owner | Complete registration scope |
| --- | --- |
| `AddAgentstrationConsoleComponents` | Shared UI/Designer, concrete context/search/palette/status adapters and designer ports. |
| `AddAgentstrationConsoleApiClients` | All Management/Work/Runtime/Flow and cleanup clients, event stream/preferences, sensitive no-retry clients, context factory and helper-owned handler/resilience configuration. |
| `AddAgentstrationConsoleRealtime` | Shared standalone realtime session/configurator and scoped Work operations client. |

`Console.Web/Configuration/ConsoleHostServiceCollectionExtensions.cs` preserves `AddAgentstrationConsoleHost`, delegating to:

| Independent Console owner | Complete registration scope |
| --- | --- |
| `AddAgentstrationConsoleOptions` | Validated API endpoint options. |
| `AddAgentstrationConsoleComponents` | Shared UI/Designer, concrete context/search/status and designer adapters. |
| `AddAgentstrationConsoleApiClients` | All API/cleanup/event/preferences clients, enumerable audience/origin targets, scoped BFF/context HTTP factories and client configuration helpers. |
| `AddAgentstrationConsoleRealtimeClient` | Independent realtime configurator and Work operations client. |
| `AddAgentstrationConsoleBffAuthentication` | Workload/session options, store/cache/token provider, cookie postconfiguration, signing handler, workload/session/delegation clients and cookie authentication. |
| `AddAgentstrationConsoleAuthorization` | Console authorization services. |

`Console.Web/Configuration/ConsolePresentationServiceCollectionExtensions.cs` owns `AddAgentstrationConsolePresentation` (Razor, localization, Problem Details/health) and `AddAgentstrationConsoleObservability` (traces/metrics and optional OTLP). These client-host registrations add no authoritative stores or execution workers.

## Workplace executable root

`Workplace.Web/Program.cs` deliberately retains its small composition: interactive Razor, shared UI/localization, recent-conversation adapter, HTTP context accessor, transient API session handler, scoped realtime configurator, typed Workplace/preferences clients with session/redirect handlers, Problem Details, health and OpenTelemetry. API/hub origin validation remains here. Module ownership for the shared/client extensions is listed above; no authoritative application/store is registered.

## Defaults, selections and contributions

Exactly-one contracts have one descriptor in the standard graph. Clock/context/GenAI defaults, the deferred profile validator, single-client resolver and Flow no-op sink use `TryAdd*`. Model management explicitly replaces the validator; configured Model Providers replace AEP security/GenAI options and the Managed resolver, while capture uses `TryAddTransient`. API composition replaces the Flow sink with its composite. Concrete Console providers/clients replace shared-component empty defaults. The independent BFF explicitly selects its scoped `IHttpClientFactory` adapter while retaining the framework singleton `IHttpMessageHandlerFactory` that owns named handlers. Runtime Run execution has one scope descriptor. Flow execution shares `WorkspaceFlowRunExecutionScope` with the ambient member of Work's ordered scope-accessor collection.

Multiple registrations below are intentional enumerable or framework contributions:

| Contract | Contribution owners/set |
| --- | --- |
| `ISecretVaultProvider` | Local encrypted and shared-key file providers in Security. |
| `IBootstrapResourceHandler` | Sixteen Infrastructure handlers plus Identity's administrator/local-account handler. |
| `IPackResourceHandler` | Eleven resource-family handlers in Packs, in explicit source order. |
| `IAepEnrollmentResourceProvisioner` | Tool and bundle provisioners in Tools. |
| `IToolExecutionEventSink` | Runtime and Flow sinks in Tools. |
| `IFlowVersionActivationGuard` | Tool Definition, Knowledge, Artifact transformation and Data Source guards in Flow. |
| `IFlowDeletionGuard` | Flow composition registers Tool Definition, Knowledge and Entry guards in that order; Work owns the concrete shared Entry guard. |
| `IWorkExecutionScopeAccessor` | Flow composition preserves the HTTP request accessor `CurrentWorkExecutionScopeAccessor`, then the ambient `WorkspaceFlowRunExecutionScope` alias. Work submission services consume this ordered collection to support request and background execution contexts. |
| Internal MCP definition/handler ports | Notification plus every explicitly listed internal Tool, sharing concrete singletons through aliases. |
| Model provider/validation/discovery/capability and extension inspection ports | AEP contributions in Model Providers preserve provider-neutral collection contracts. |
| `IParameterUsageProvider` | Model Provider reference checks from Model management. |
| `IAuthorizationHandler` | Permission/resource-permission, Platform administrator and interactive-user handlers in Identity API. |
| `BffDelegationTarget` | Named-client audience/origin targets in independent Console client composition. |
| Framework options, HTTP clients, EF configuration, authentication schemes and telemetry/logging providers | Named/configuration contributions owned by their listed extension. |
| `IHostedService` | Explicit scheduler, recovery, cleanup, security-audit and host-worker contributions below. |

Quartz hosting preserves its significant order: logging initialization, SQLite schema initialization when selected, Quartz hosted scheduler, then Trigger reconciliation. Infrastructure separately owns staged Artifact cleanup and Data Source acquisition recovery. The host adds Work execution, assignment lease reaping, Flow recovery and Source refresh. Identity API's BFF trust-audit service retains its independent lifecycle.

Testing disables the optional application/scheduler worker set by default; `Agentstration:Testing:HostedServicesEnabled` enables it. `Agentstration:Testing:OpenTelemetryEnabled` independently controls server telemetry. The transport security audit service remains registered under either worker setting. Cleanup retains ownership of only its private paths/pools. Endpoint mapping and ordered initialization remain outside registration.

Descriptor tests inspect service type, lifetime, selection and cardinality before provider construction, then validate SQLite/PostgreSQL and deterministic/Managed graphs with `ValidateOnBuild` and `ValidateScopes`. Host lifecycle tests cover workers/telemetry. New registrations belong in the appropriate owner and must update tests when these contracts change.
