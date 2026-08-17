# ADR-0049: Workplace Dashboards own Entry composition

Status: Accepted — 2026-08-17

## Context

`WorkplaceWorkspace` previously mixed the functional work boundary with the visual arrangement of Entries. That made the Workspace itself behave like a single hard-coded Home screen and encouraged the `personal` Workspace name to acquire accidental product semantics. Published Entries are already normalized to immutable published Flow references and Pack Entries carry namespaces.

## Decision

Workspace owns the work context. Dashboard owns presentation and Entry composition. Entry owns the user-facing invocation contract. Flow owns execution.

A Workspace may contain any number of Dashboards with identical capabilities. Exactly one published Dashboard is the default; `home` is only the seeded default Dashboard name and has no engine semantics. `personal` is only a Workspace name and does not restrict capabilities.

Each Dashboard references Entries by their complete namespaced `EntryId`, role (`Primary`, `Featured`, or `Standard`), and order. A Dashboard allows zero or one Primary Entry and cannot contain the same Entry twice. The same Entry may appear in several Dashboards. Installing a Pack makes its published Entries available to the catalog but never exposes them automatically in a Dashboard.

Dashboard does not know Agent, Flow kind, routing, workflow, orchestration, provider, deployment, or runtime. Invocation remains:

```text
Dashboard → Entry → EntryResolvedTarget → immutable FlowReference → FlowRun → Runtime
```

Workplace resources use the stable `agentstration.io/v1` resource contract. This change does not introduce a date-based HTTP API version or an `api-version` query discriminator.

## Consequences

`WorkplaceWorkspace` and its draft contain no Entry composition. Dashboard drafts and published Dashboards have independent repository, SQLite, API, client, Console, and Workplace projections. Tasks, Interactions, Conversations, and Notifications remain Workspace-owned and are unaffected when the selected Dashboard changes.

Existing development data is reseeded directly into `personal` plus its default `home` Dashboard. There is no migration of the former Workspace composition, compatibility projection, or legacy fallback.
