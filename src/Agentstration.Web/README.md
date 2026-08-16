# Agentstration Web console

The Console exposes Entry authoring at `/entries`. An administrator can select an Agent or Flow, preview with the same renderer as Workplace without executing it, validate references, inspect dependencies, and publish a pinned Flow target. Agent selection automatically creates or versions a hidden Direct Agent Flow; the Console never calls Runtime directly for Entry execution.

`Agentstration.Web` is the official administration and operations console for Agentstration. It remains the existing single ASP.NET Core host: REST, MCP, workers, and the Blazor UI ship as one executable modular monolith.

## Stack and rendering

- .NET 10, ASP.NET Core, Blazor Web App, and Razor Components.
- Interactive Server is applied at the router, so navigation, filters, theme, and notification state are interactive by default.
- Initial HTML is server rendered. No global WebAssembly or Auto render mode is enabled.
- The console uses native Razor and CSS; it has no JavaScript UI framework or heavy component library.

`Agentstration.Web.Components` is a Razor Class Library containing the shell, responsive navigation, light/dark themes, focused state services, and reusable operational components. It has no dependency on the Agentstration domain or persistence projects, which keeps it suitable for a possible future MAUI Blazor Hybrid host.

The current design system includes `StatusBadge`, `HealthIndicator`, `MetricCard`, `PageHeader`, `EmptyState`, `LoadingState`, `ErrorState`, `ConfirmationDialog`, `SearchBox`, `FilterBar`, `DataGrid`, `EventList`, and `ExecutionTimeline`.

## Run

```powershell
dotnet run --project src/Agentstration.Web
```

Open `http://localhost:5100`. Port `5080` belongs to Work API. The console provides Overview, Management, Agents, Model Profiles, Model Providers, Runtime, Tasks, Flows, Executions, Events, and Settings. Existing Workspaces, Ingest, and Missions routes remain available for the original content vertical.

## Management resource editing

The Management area provides declarative CRUD workflows for agents and logical model profiles:

- list, filter, create, edit, and delete canonical `Agent` resources;
- select an available Agent by logical name;
- select a model profile through a reusable picker that persists only its canonical resource ID;
- edit tags as `key=value` pairs;
- preserve ETags and send `If-None-Match` for creation and `If-Match` for updates/deletion;
- surface Problem Details and prevent silent overwrites on HTTP 412 conflicts.

`/modelproviders` displays configured providers, health and dynamically discovered models without persisting provider models. `/modelprofiles` provides searchable canonical profile CRUD, provider/model selection, full generation/reasoning/output options, usage inspection, effective resolution, declarative JSON, ETag conflict recovery, and deletion protection. `/runtimeprofiles` provides the equivalent CRUD surface for runtime type, sessions, tool invocation, streaming and adapter options. Agent details deliberately separate the declared profile from the provider and model resolved through `/api/agents/{name}/model`; the Agent Runner displays the deployment runtime profile and exposes streaming for advanced runs.

Agent and model management always delegate to the canonical Management HTTP API, which remains the authority for validation, generations, provisioning state, and lifecycle events. `MockApiClient` is retained only for unrelated demonstration projections. Tasks are never simulated: `/tasks` always uses the configured Work API.

## Agent Runner

Each persisted agent exposes a **Run** action leading to `/agents/{agentName}/run`. Quick Run sends an exact agent generation to `POST /api/runtime/runs`, then consumes `/events` as Server-Sent Events. The page keeps response, execution metadata, messages, tool calls, trace, raw JSON and recent per-agent history together.

Stopping a run calls the Runtime cancellation endpoint. Retry creates a new Run from the original payload. Direct console Runs never create Work Items. Advanced context, JSON runtime parameters and timeout are accepted now; final policy resolution remains the Runtime and Model Profile responsibility.

The agent editor and Runner use dedicated real HTTP clients even when `UseSimulatedData=true`, ensuring that saved and activated generations come from the same persisted resource. Saving calls the idempotent activation endpoint; the Runner displays model resolution and exact-generation deployment readiness and exposes **Reconcile runtime** for a manual retry. Runtime parameters are restricted to `temperature` and `maxOutputTokens`; profile defaults are merged in Runtime and the effective values are passed to MAF and displayed with the completed Run.

## API configuration

Console data access is isolated behind `IManagementApiClient`, `IModelProvidersClient`, `IModelProfilesClient`, `IAgentsModelClient`, `IRuntimeApiClient`, `IWorkApiClient`, and `IFlowApiClient`. HTTP implementations use `HttpClientFactory`, explicit timeouts, limited retry, total request timeout, and circuit-breaking defaults from the standard .NET resilience handler. API failures surface a safe error identifier in the UI.

The development default may use simulated data for legacy dashboard, Flow, and general Runtime projections while Agent/model management and Task supervision remain canonical:

```json
{
  "Agentstration": {
    "WorkApi": { "BaseAddress": "http://localhost:5100/" },
    "WorkplaceBaseUrl": "http://localhost:5180/"
  }
}
```

Work API may be unavailable at Console startup. The rest of the Console remains usable and `/tasks` presents an explicit retry state without demo fallback. `WorkplaceBaseUrl` is optional; when omitted, Workplace deep links are hidden.

```json
{
  "Agentstration": {
    "UseSimulatedData": true,
    "ManagementApi": { "BaseAddress": "http://localhost:5100/", "TimeoutSeconds": 15 },
    "RuntimeApi": { "BaseAddress": "http://localhost:5100/", "TimeoutSeconds": 15 },
    "WorkApi": { "BaseAddress": "http://localhost:5100/", "TimeoutSeconds": 15 },
    "FlowApi": { "BaseAddress": "http://localhost:5100/", "TimeoutSeconds": 15 }
  }
}
```

Set `Agentstration__UseSimulatedData=false` and configure each base address to activate the remaining typed HTTP clients. Work Task supervision, Agent CRUD, model-provider, model-profile and agent-model-resolution screens always use canonical APIs; unrelated dashboard areas may still use `MockApiClient` in demonstration mode. Secrets are never passed to Razor components or browser code.

The existing backend does not yet expose every runtime projection required by the console. In HTTP mode, the Runtime client verifies `/health` and reports only the local runtime shell; detailed resource and execution projections will replace this adapter when public Runtime endpoints land.

## Events and SignalR

Pages depend on `IAgentstrationEventStream`, not a transport implementation. The simulated implementation supplies a deterministic recent-event feed. The HTTP implementation is intentionally empty until the platform publishes its SignalR Hub contract; a future Hub client can replace it without changing pages or design-system components.

## Authentication

Local launch uses ASP.NET Core Identity with a dedicated SQLite credential store. A fresh instance redirects to the one-time, server-rendered `/bootstrap` page; no default account or password is created. `/login`, `/logout`, and `/access-denied` stay outside the protected Interactive Server circuit so the application cookie is established at the normal ASP.NET Core HTTP boundary. Forms use antiforgery validation, and return URLs are restricted to local paths. The JSON bootstrap and login endpoints under `/api/auth` remain available for programmatic clients. `Local`, `Oidc`, and `Hybrid` modes converge toward the same Agentstration `Principal` and Workspace policies. The isolated `Development` handler remains available only through explicit Development/Testing configuration.

`identity.db` is upgraded with versioned EF Core migrations. The persistent Data Protection key ring defaults beside the local data file and can be changed with `Agentstration:Authentication:DataProtectionKeysPath`. Back up and protect both stores; the key directory contains sensitive material required to keep cookies and Identity lifecycle tokens valid across restarts.

Platform administrators can list, create, enable, and disable local accounts from **Organization > Members** or `/api/identity/accounts`. Member details manage the current Workspace role through `/api/identity/workspaces/{workspaceId}/memberships`; the service prevents removal or demotion of the final Owner.

Interactive Server components invoke canonical APIs through server-side typed clients. For the standalone same-instance endpoints, `ForwardSessionCookie=true` propagates only the authenticated Agentstration session and resolved Workspace to the exact configured origin; redirects are disabled and unrelated cookies are never copied. The APIs still execute their authentication and authorization policies. Keep this option off for unrelated or independently deployed APIs and use OAuth Bearer access tokens at that boundary.

## Tests

`Agentstration.Web.Tests` covers API client mapping, model discovery, profile filtering and selection rules, conditional request headers, Problem Details, editor and runner payload mapping, simulated CRUD, SSE processing, retry, and dashboard aggregation. `Agentstration.Web.Components.Tests` covers focused UI state services. Both use MSTest and remain offline.
