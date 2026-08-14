# ADR-0036 — Runtime resolution and control-plane hardening

Status: Accepted

## Context

Runtime execution previously read Management resources through `IControlPlaneStore`, while the SQLite adapter needed a type switch for every resource kind. Transitional ARM-like identifiers and a standalone `McpServerResource` also preserved concepts superseded by the native resource envelope and ToolProvider discovery.

## Decision

- Runtime Core resolves `RuntimeAgentReference` through `IRuntimeAgentResolver` and consumes only `ResolvedRuntimeAgent`/`ExecutableAgentDefinition` contracts owned by Runtime Abstractions.
- The Management adapter translates immutable agent revisions and ready deployments into the runtime view. Runtime Core and Runtime Abstractions do not reference Management assemblies or `IControlPlaneStore`.
- Routing plus immediate execution is an application coordination concern implemented by `AgentExecutionCoordinator`, outside `AgentManagementService`.
- Control-plane resources are keyed explicitly by `(Kind, metadata.name)`. `ResourceIdentifier`, provider namespaces, and the CLR-to-Kind registry are removed. Cross-workspace references use `ResourceReference.workspaceRef`; ResourceGroup is not part of the model.
- Common store state is applied through the base `Resource` record, so UID, tenant/workspace scope, ETag, and resourceVersion do not require concrete-type switches.
- A request-context access mode is explicit: workspace operations are scoped, system operations use an explicitly registered `SystemOperationRequestContext`, and `AddSqliteControlPlane` defaults to an unavailable context that fails closed for reads and writes.
- MCP servers are configured only as `ToolProvider` resources with `providerType: Mcp`; discovery materializes governed `Tool` resources. The standalone `McpServerResource` and direct MCP tool mapping are removed.

## Consequences

New runtime adapters depend on Runtime Abstractions, while Management-side resolvers own all control-plane translation. Extension resource kinds can be persisted without modifying a central registry or the SQLite adapter. The standalone composition uses `CurrentRequestContext`, remains unavailable until local identity bootstrap initializes its workspace fallback, and never receives implicit system access. `Name` remains a read-only convenience projection; the legacy `Id` and typed `Properties` projections are removed, and all resource access uses `Metadata` and `Definition`.

The built-in agent queries use SQLite JSON expression indexes and server-side `json_extract` predicates for revision identity, deployment revision, and deployment agent. `ListDeploymentsAsync` deliberately remains a scoped full-kind query because reconciliation and routing request the complete deployment inventory; it has no arbitrary item limit. `AgentExecutionCoordinator` remains in Infrastructure as the pragmatic composition-level orchestrator across Management, routing, and Runtime, and does not move execution responsibility back into Management Core.

`FlowRunService` still contains the graph execution engine. This iteration isolates draft-to-snapshot compatibility and removes legacy path parsing, but leaves the larger engine extraction for a focused follow-up to avoid combining it with the runtime and resource-model boundary change.
