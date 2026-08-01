# ADR-0009: Independent Work Plane with local Runtime dispatch

## Decision

Add `Agentstration.Work` as a framework-neutral domain boundary. It owns `WorkItem`, controlled lifecycle transitions, functional history, interactions, results, errors, and idempotent application of Runtime events. Public DTOs, storage, and transport remain separate.

The Work Plane delegates through `IWorkExecutionGateway`. Standalone mode implements this port in `Agentstration.Runtime.Local` with a bounded local queue and a hosted adapter. Work snapshots use an independent SQLite store with optimistic version concurrency.

## Consequences

The Work Plane does not reference Microsoft Agent Framework, a model provider, Runtime implementation, ASP.NET Core, or EF Core. The local adapter is executable offline but does not recover queued messages after process failure. A durable connector can replace it without changing the aggregate or canonical `/api/work/workitems` contracts.
