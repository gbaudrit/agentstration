# Dependency injection composition

Agentstration uses explicit `IServiceCollection` extensions as the executable
map of its modular-monolith boundaries. Registration is intentionally not
assembly-scanned: optional providers, storage selection, security policies,
hosted services, and ordered multi-bindings must remain visible during review.

## Composition roots

| Root | Responsibility |
|---|---|
| `Agentstration.Web/Program.cs` | Resolve host configuration, invoke `AddAgentstrationWebHost`, map transports, and run the ordered startup lifecycle. |
| `Agentstration.Workplace.Web/Program.cs` | Resolve API endpoints, invoke `AddAgentstrationWorkplaceHost`, and map the standalone Workplace UI. |
| `Agentstration.AppHost/Program.cs` | Compose Aspire resources and pass configuration to the executable hosts and AEP extensions. |
| AEP extension `Program.cs` files | Register only the provider hosted by that autonomous extension process. |

Endpoint mapping, database initialization, bootstrap application, extension
discovery, and application start are lifecycle operations. They remain outside
service-registration extensions.

## Platform registration ownership

`AddAgentstration(AgentstrationServiceRegistrationOptions)` is the public
platform façade. It validates and normalizes the host inputs once, then invokes
the following Infrastructure-owned extensions in dependency order.

| Extension | Owned registrations |
|---|---|
| `AddAgentstrationFoundation` | Time, request context, Management events, AI defaults, and direct chat-client fallback. |
| `AddAgentstrationControlPlane` | Storage options, platform initializer, and the selected SQLite or PostgreSQL control-plane store. |
| `AddAgentstrationSecurityAndBootstrap` | Secret vaults, identity/authorization services, audit, topology bootstrap, and bootstrap resource handlers. |
| `AddAgentstrationAgentRuntime` | Agent compilation and resolution, MAF materialization, Runtime registry and queue, deployment provisioners, routing, and MCP tools. |
| `AddAgentstrationPacks` | Pack archive/artifact services, resource handlers, composition, authoring, and management. |
| `AddAgentstrationSources` | Manifest retrieval, verification index, snapshots, compatibility, catalogs, and Source management. |
| `AddAgentstrationToolingAndTriggers` | Tool resources, hooks, Trigger services, Quartz configuration, and optional scheduler hosted services. |
| `AddAgentstrationRuntimeRuns` | Selected Runtime Run store, Run lifecycle, Tool execution pipeline, event sinks, and audit reader. |
| `AddAgentstrationWorkPlane` | Selected Work store, artifact store, execution queue/gateway, Work Items, Workplace, and task projection. |
| `AddAgentstrationFlowPlane` | Selected Flow store, definitions, Entries, Run queue, execution scopes, expressions, orchestration, and retention. |

Storage implementation projects continue to own their provider-specific
extensions. Infrastructure selects one provider once and calls those methods;
it does not reproduce EF Core registration details.

Existing module-owned extensions remain authoritative:

| Owning project | Extensions |
|---|---|
| Management Core | `AddAgentstrationModelManagement` |
| Model Providers | `AddAgentstrationModelProviders` |
| MCP tools | `AddAgentstrationMcpTools` |
| Identity | `AddAgentstrationLocalIdentity`, `AddAgentstrationPostgreSqlIdentity` |
| Management storage | `AddSqliteControlPlane`, `AddPostgreSqlControlPlane` |
| Runtime storage | `AddSqliteRuntimeRuns`, `AddPostgreSqlRuntimeRuns` |
| Work storage | `AddSqliteWorkPlane`, `AddPostgreSqlWorkPlane` |
| Flow storage | `AddSqliteFlowStorage`, `AddPostgreSqlFlowStorage` |
| Shared Web UI | `AddAgentstrationWebComponents`, `AddAgentstrationLocalization`, `AddAgentstrationFlowDesigner` |
| Workplace client | `AddAgentstrationWorkplaceClient` |
| AEP ASP.NET Core | `AddAgentstrationAep` and its explicit contribution extensions |

The string-parameter `AddAgentstration` overload remains a compatibility
façade. New composition code should use
`AgentstrationServiceRegistrationOptions` so adding a host setting does not
extend an ordered parameter list.

## Server and Console ownership

`AddAgentstrationWebHost` composes the platform façade with focused server
registrations:

- Model Provider and Management services;
- extension discovery and AEP enrollment;
- HTTP/Razor/SignalR/MCP transport;
- the selected ASP.NET Core Identity store;
- bootstrap host services;
- realtime projections;
- Console composition;
- optional background workers and test cleanup.

`AddAgentstrationWebConsole` remains the convenient Console façade and delegates
to separate component, HTTP/realtime client, authentication, and authorization
registrations. `AddAgentstrationObservability` owns Web logging, tracing, and
metrics. Workplace follows the same pattern through
`AddAgentstrationWorkplaceHost` and
`AddAgentstrationWorkplaceObservability`.

## Registration semantics

- `TryAdd*` denotes a genuine standalone/test fallback that a fuller host may
  replace.
- `Replace` denotes an intentional exactly-one production selection, including
  the configured GenAI options, the Management-backed Model Profile validator,
  and the server's composite Flow event sink.
- Repeated `Add*` calls for the same contract are allowed only for intentional
  `IEnumerable<T>` contributions. Current examples include secret vaults,
  bootstrap and Pack handlers, agent deployment provisioners, and Tool
  execution event sinks.
- Service lifetimes are part of the composition contract. Refactoring a
  registration into another extension must not silently change its lifetime.
- Registration-contract tests inspect descriptor cardinality before provider
  construction and build representative providers with scope/build validation.

These rules prevent the default container's last-registration-wins behavior
from hiding accidental duplicates or undocumented caller-order dependencies.
