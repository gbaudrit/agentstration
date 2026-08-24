# ADR-0071: Remove the legacy content and mission vertical

Status: Accepted — 2026-08-24

## Context

The repository still contained an early product vertical built around GUID content Workspaces, Inboxes, ingested Items, derived Memory entries and scheduled Missions. It exposed REST, Razor and built-in MCP surfaces, ran an item-processing worker, and persisted the aggregate in `.agentstration/data.json`. A separate inactive Mission worker and PostgreSQL prototype remained as historical implementation artifacts.

The current architecture instead has canonical Management Workspaces, Work and Workplace interactions, versioned Flows, durable Runtime Runs, governed Agents and Packs, and Trigger scheduling through Quartz. No Agentstration version is deployed and no external consumer or production data migration must be preserved.

## Decision

Remove the legacy vertical completely rather than deprecating or feature-flagging it. This includes its HTTP, Razor and MCP surfaces; workers and demo seed; Application services and ports; Domain and transport contracts; JSON, in-memory and PostgreSQL stores; content-specific evaluation project; tests; telemetry; packages; and `Data:Path` configuration.

The generic MCP infrastructure remains because governed external MCP and AEP Tool Providers use it. The modern `/api/workspaces/{workspaceName}` Workplace routes, canonical Management Workspaces, Work, Flow, Runtime, Trigger, Pack, Agent, identity and authorization modules remain unchanged. Persistent module data now derives from `Data:Directory`; startup must not create or depend on `data.json`.

## Consequences

- The legacy content, memory and Mission APIs, pages and MCP tools are no longer available.
- There is no compatibility shim, deprecation interval or production migration.
- `Agentstration.Domain`, `Agentstration.Contracts`, `Agentstration.Evaluation` and the content evaluation test project are removed because they became empty.
- PostgreSQL packages are removed from `Agentstration.Infrastructure`; current module persistence remains SQLite-backed.
- New ingestion, semantic memory or observation capabilities must be designed against the current Management, Work, Flow, Runtime, Tool and Trigger boundaries rather than reviving the deleted aggregate.
