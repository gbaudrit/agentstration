# ADR-0020: Workplace Entry, Interaction, and Task vertical

Status: Accepted — 2026-08-06

Workspace resource ownership and Entry composition ownership in this decision are superseded by [ADR-0049](0049-workplace-dashboards-own-entry-composition.md). Interaction and Task decisions remain in force.

## Decision

Add the end-user Workplace as a distinct experience inside the modular-monolith host. `Agentstration.Work` owns ARM-inspired Workspace and Entry resources, Interaction state, structured UI actions, and the user-facing WorkTask projection. SQLite persistence remains in the independent Work store. Reusable Razor components and HTTP/SignalR clients live in `Agentstration.Workplace.Components` and `Agentstration.Workplace.Client`.

At the time of this decision, Entry presentation roles were stored on the Workspace. ADR-0049 moves that responsibility to Dashboard while retaining the single Entry submission service. Submissions may create an Interaction without a Task, or correlate the Interaction with the existing durable Work aggregate.

For Flow-backed work, the local Work worker creates a durable Flow Run and maps its terminal result back to the Work aggregate. Workplace observes functional Task changes through a Workspace-scoped SignalR hub and reloads authoritative state from REST after reconnect.

## Consequences

Console and Workplace have separate layouts and product vocabulary without creating a second deployable process. Existing WorkItem APIs remain compatible, while new public endpoints use Tasks, Entries, Interactions, and Workspaces. Flow and Runtime details remain hidden from the default Workplace UI. A later standalone Web or MAUI host can reuse the new components and client without referencing providers, Azure, Foundry, EF Core, or runtime implementations.

The Iteration 3 UX keeps Entry presentation declarative. `Primary` is a visual role, now owned by Dashboard under ADR-0049, and is rendered through the same generic Prompt/Form renderer as `Standard`; a container supplies emphasis without creating a specialized Primary Entry domain type. The user journey is presented continuously from Entry to Interaction, inline PendingAction, Task, Result, and Artifact. Suggestions only populate an Entry field and never bypass explicit user submission. Public UI hides technical execution and storage identifiers.

ADR-0022 extends this lifecycle by making the Interaction durable after Task completion. It does not change Entry ownership or the separation between functional WorkTask projection and technical FlowRun execution.
