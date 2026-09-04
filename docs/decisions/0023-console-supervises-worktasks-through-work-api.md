# ADR-0023: Console supervision of WorkTasks through Work API

## Status

Accepted — 2026-08-06

## Context

The Console `/work` page displayed four invented Work items supplied by `MockApiClient`, applied filters in memory, and exposed UI-only priority and owner values. Those objects were unrelated to the durable Interactions, WorkTasks, FlowRuns, results, and artifacts created by Workplace. Keeping both projections would make the Console an ambiguous second owner of Work state.

## Decision

Work API is the sole source of truth for operational Tasks. The Console is an HTTP and SignalR client and never references Work SQLite, repositories, `WorkplaceService`, execution workers, or Workplace internals.

Work API exposes a public `/api/tasks` supervision surface with server pagination, bounded page size, filters, search, sorting, counters, a compact list contract, an aggregated detail contract, and explicit subresources. List responses exclude conversation bodies, result contents, artifact contents, traces, and Runtime payloads. Artifact storage keys never cross the supervision contracts or SignalR events; downloads use the existing workspace-scoped Work API endpoint.

The Work SQLite adapter maintains indexed query columns for Workspace, status, Interaction, Entry, continuation parent, and current FlowRun alongside the canonical WorkItem snapshot. Existing databases are upgraded and backfilled during local initialization. This is a Work-owned query projection, not Console persistence.

The Console always registers the real typed Work API client, even when unrelated legacy dashboard projections use deterministic demonstration data. `/tasks` replaces `/work`; `/work` remains only a route alias. SignalR updates a targeted row or open detail, deduplicates event IDs, reconnects automatically, and performs an HTTP resynchronization after reconnect. HTTP remains authoritative when realtime is offline.

Workplace deep links are generated only from optional configuration. The Console is read-only for Interactions and PendingActions. Pause, resume, and cancel are sent to existing Work API commands; Task creation, conversation, retry, mutation, and deletion are not Console capabilities.

## Amendment — 2026-09-04

Workspace cleanup may delete terminal Tasks through the canonical Work API. The operation requires the Run deletion permission and a current `If-Match` ETag, rejects non-terminal Tasks, and removes the Task family together with its Work-owned activities, pending actions, results, artifact metadata and notifications. Artifact payloads are removed from the workspace artifact store. A retained Interaction is detached from the deleted Task so its conversation remains available without a dangling current-Task reference. Ordinary Task supervision remains non-destructive outside the cleanup surface.

## Consequences

- Console can run without Workplace and show previously persisted Tasks.
- Work API unavailability degrades only the Tasks section; no fake fallback is displayed.
- Continuation FlowRuns remain children of immutable terminal FlowRuns and appear in the same public Task.
- Query performance is bounded by server pagination and SQLite indexes.
- The former static Task list, client-side counting, artificial owner/priority, creation button, and simulated Work delay are removed.
