# Authoritative server components

The canonical [interactive C4 L3 view](../interactive.mdx) opens the **Authoritative server and API** container into stable responsibilities. It deliberately does not mirror every project, namespace, endpoint, worker, or class.

| L3 component | Responsibility | Principal implementation areas |
| --- | --- | --- |
| API transport | Family-owned REST, MCP, SignalR, OpenAPI, request-context, and HTTP security adapters | `Agentstration.Api`, `Agentstration.*.Api` |
| Identity and authorization | Authentication, Principals, Workspace access, RBAC, PATs, delegation, and security audit | `Agentstration.Identity`, `Agentstration.Identity.Contracts`, `Agentstration.Security.Contracts` |
| Resource-family services | Family-owned resource validation, lifecycle, desired state, and provider-neutral ports | `Agentstration.Agents`, `Agentstration.Models.Application`, `Agentstration.Triggers`, `Agentstration.Extensions.Aep`, `Agentstration.Sources`, `Agentstration.Secrets` |
| Distribution and bootstrap | Bootstrap profiles, Pack installation, and generic manifest planning delegated to owning families | `Agentstration.Bootstrap.Contracts`, `Agentstration.ResourceManagement.Contracts`, `Agentstration.Packs` |
| Work and Workplace application | Functional Work lifecycle, interactions, notifications, projections, and root execution submission | `Agentstration.Application`, `Agentstration.Work`, `Agentstration.Work.Contracts` |
| Flow engine | Flow definitions, immutable publications, durable Flow Runs, and provider-neutral graph execution | `Agentstration.Flows.*` |
| Runtime execution | Durable technical Runs, agent materialization, model resolution, attempts, and normalized events | `Agentstration.Runtime.*`, `Agentstration.Infrastructure` runtime adapters |
| Tool execution pipeline | Governed Tool catalog, hooks, approvals, MCP publication, and invocation | `Agentstration.Tools`, `Agentstration.Tools.Mcp` |
| Scheduling and background processing | Trigger projection, reconciliation, source refresh, recovery, and bounded hosted work | `Agentstration.Infrastructure` workers, `Agentstration.Web` hosting composition |

The mapping is supporting evidence, not the identity of a C4 component. A responsibility may span several projects, and a composition project may connect several responsibilities without owning their business behavior.

The principal dependency chain is Work → Flow → Runtime for execution. Flow and Runtime both use the governed Tool execution pipeline; Runtime resolves immutable Agent and profile resources before constructing an executable agent. API transport delegates to these application boundaries instead of implementing business logic. Background processing opens explicit scopes and reuses the same services.

The [dynamic architecture views](../runtime/dynamic-views.md) show the principal implemented interactions across these responsibilities without duplicating their structural ownership.

Relevant decisions include [ADR-0005](../../decisions/0005-shared-application-services.md), [ADR-0008](../../decisions/0008-reconstructible-maf-runtime.md), [ADR-0009](../../decisions/0009-independent-work-plane.md), [ADR-0010](../../decisions/0010-independent-flow-module.md), [ADR-0055](../../decisions/0055-agentstration-owns-tool-execution-boundary.md), [ADR-0109](../../decisions/0109-control-plane-composes-resource-family-modules.md), and [ADR-0110](../../decisions/0110-api-transport-is-composed-from-family-modules.md).
