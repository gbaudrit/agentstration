# Agentstration Web console

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

Open `http://localhost:5080`. The console provides Overview, Management, Runtime, Work, Flows, Agents, Executions, Events, and Settings. Existing Workspaces, Ingest, and Missions routes remain available for the original content vertical.

## Management resource editing

The Agents section provides a complete declarative CRUD workflow:

- list, filter, create, edit, and delete canonical `Agentstration.Agents/agents` resources;
- select an available AgentType and retain its explicit version;
- edit model profile and tool references as canonical resource IDs;
- edit tags as `key=value` pairs;
- preserve ETags and send `If-None-Match` for creation and `If-Match` for updates/deletion;
- surface Problem Details and prevent silent overwrites on HTTP 412 conflicts.

The simulated client implements the same mutable workflow in memory. In HTTP mode the UI delegates to the Management API, which remains the authority for validation, generations, provisioning state, and lifecycle events.

## Agent Runner

Each persisted agent exposes a **Run** action leading to `/agents/{resourceGroup}/{agentName}/run`. Quick Run sends an exact agent generation to `POST /api/runtime/runs`, then consumes `/events` as Server-Sent Events. The page keeps response, execution metadata, messages, tool calls, trace, raw JSON and recent per-agent history together.

Stopping a run calls the Runtime cancellation endpoint. Retry creates a new Run from the original payload. Direct console Runs never create Work Items. Advanced context, JSON runtime parameters and timeout are accepted now; final policy resolution remains the Runtime and Model Profile responsibility.

## API configuration

Console data access is isolated behind `IManagementApiClient`, `IRuntimeApiClient`, `IWorkApiClient`, and `IFlowApiClient`. HTTP implementations use `HttpClientFactory`, explicit timeouts, limited retry, total request timeout, and circuit-breaking defaults from the standard .NET resilience handler. API failures surface a safe error identifier in the UI.

The development default is simulated data:

```json
{
  "Agentstration": {
    "UseSimulatedData": true,
    "ManagementApi": { "BaseAddress": "http://localhost:5080/", "TimeoutSeconds": 15 },
    "RuntimeApi": { "BaseAddress": "http://localhost:5080/", "TimeoutSeconds": 15 },
    "WorkApi": { "BaseAddress": "http://localhost:5080/", "TimeoutSeconds": 15 },
    "FlowApi": { "BaseAddress": "http://localhost:5080/", "TimeoutSeconds": 15 }
  }
}
```

Set `Agentstration__UseSimulatedData=false` and configure each base address to activate the typed HTTP clients. Simulated data is implemented only by `MockApiClient`; pages consume the same interfaces in both modes. Secrets are never passed to Razor components or browser code.

The existing backend does not yet expose every runtime projection required by the console. In HTTP mode, the Runtime client verifies `/health` and reports only the local runtime shell; detailed resource and execution projections will replace this adapter when public Runtime endpoints land.

## Events and SignalR

Pages depend on `IAgentstrationEventStream`, not a transport implementation. The simulated implementation supplies a deterministic recent-event feed. The HTTP implementation is intentionally empty until the platform publishes its SignalR Hub contract; a future Hub client can replace it without changing pages or design-system components.

## Authentication

Local launch uses an explicitly configured development authentication handler. Authorization policies named `Viewer`, `Operator`, and `Administrator` are registered now. Set `Agentstration:Authentication:DevelopmentMode` to `false` when wiring the host to OpenID Connect/Microsoft Entra ID; no Entra tenant is required for local startup.

## Tests

`Agentstration.Web.Tests` covers API client mapping, conditional request headers, Problem Details, editor and runner payload mapping, simulated CRUD, SSE processing, retry, and dashboard aggregation. `Agentstration.Web.Components.Tests` covers focused UI state services. Both use MSTest and remain offline.
