# ADR-0110 — API transport is composed from family-owned modules

## Status

Accepted

## Context

ADR-0109 assigns business behavior to plural resource families, but a single endpoint assembly would still allow every transport handler to depend on every implementation. Splitting deployment by family would conflict with the modular-monolith model.

## Decision

Every family with a public HTTP, MCP, or SignalR surface owns an `Agentstration.{Family}.Api` project. Each module exposes explicit `Add{Family}Api` and `Map{Family}Api` extensions. `Agentstration.Api` is a lightweight aggregator that calls those extensions in a visible order and retains only global health and OpenAPI conventions. `Agentstration.Web` remains the sole executable host.

Transport ownership is:

| Module | Surface |
| --- | --- |
| `Agents.Api` | Agent configuration, revisions and deployments |
| `Triggers.Api` | Trigger configuration; occurrence execution and history in separate files |
| `Flows.Api` | Flow authoring/publication; FlowRun execution/history and hub in separate files |
| `Models.Api` | Model providers, profiles and diagnostics |
| `Tools.Api` | Tool providers, definitions, hooks, governance audit and MCP |
| `Secrets.Api` | Secret and vault administration |
| `Runtime.Api` | Runtime profiles, readiness and direct Runtime Run execution |
| `Packs.Api` | Pack lifecycle plus Pack-owned Source preview/install operations |
| `Sources.Api` | Source providers, Sources and Source Registry administration |
| `Resources.Api` | generic resource-scope projections |
| `Identity.Api` | authentication, authorization, accounts and identity administration |
| `Bootstrap.Api` | composed bootstrap profile API |
| `Extensions.Api` | extensions and AEP enrollment |
| `Work.Api` | Work resource operations |
| `Workplace.Api` | Workplace administration, interactions/tasks, operations and hub |

Shared policy names, principal features and linked global usings are family-neutral and live outside the aggregator implementation. API modules may consume their family, public contracts and abstract ports, but cannot reference the executable Web host, Console projects, or concrete SQLite/PostgreSQL adapters.

Cross-family Work and Flow operational reads are owned by `IWorkOperationsQueryService`; its implementation returns complete projections. The endpoint only translates HTTP inputs and outputs. Bootstrap, Source Console and resource-scope application adapters live in Infrastructure rather than the aggregator.

## Consequences

Public routes, policies, ETags, Problem Details, pagination and OpenAPI remain one compatible surface. Endpoint ownership is compiler-visible without reflection or additional deployables. Adding a public family surface requires an explicit module and aggregator registration, while architecture tests prevent endpoint code from returning to Web or the aggregator.
