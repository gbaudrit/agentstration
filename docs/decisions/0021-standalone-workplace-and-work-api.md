# ADR-0021: Standalone Workplace and Work API hosts

Status: Accepted — 2026-08-06

## Context

ADR-0020 introduced the Workplace vertical inside the modular-monolith process and intentionally deferred a deployable split. The second increment requires the end-user application to run and publish without the operations Console, while preserving local-first execution and the existing domain/application boundaries.

## Decision

Create two deployable projects: `Agentstration.Work.Api` owns the public Work/Workplace HTTP surface, Workspace-scoped SignalR hub, local execution workers, and composition of application/infrastructure adapters; `Agentstration.Workplace.Web` owns only the end-user Blazor experience. Workplace Web communicates exclusively through its typed HTTP and SignalR client and references neutral contracts/components only. `Agentstration.Web` remains the independent operations Console.

Aspire declares Console, Work API, and Workplace as separate resources. Direct launch and publish of Work API and Workplace do not require the Console. Deterministic AI, SQLite, in-process workers, and filesystem artifacts remain the offline defaults; the change does not introduce a broker or microservice-only runtime requirement.

Workspace is the sole Workplace isolation boundary. No Tenant/User/Profile/Auth domain is introduced. Pending actions persist server-side continuation state and only a hash of their single-use resume token; public DTOs and events exclude that hash.

Workplace reuses the Console design system through the provider-neutral `Agentstration.Web.Components` library, not through a reference to the Console host.

Iteration 3 adds no deployment or service boundary. The standalone Workplace performs targeted SignalR-driven refreshes for the active Interaction, Task, and notification summary, exposes a subtle reconnecting state, and continues to treat REST as authoritative. Raw pending-action resume tokens are returned only to the initiating response; persisted Interactions, public pending-action DTOs, and events contain no recoverable token. A page reload therefore cannot recover an unresolved raw token in this unauthenticated increment and the user starts a new request.

ADR-0022 adds conversational continuation entirely within the same standalone boundary. Message acceptance, child execution correlation, Interaction history, and successive Task outputs remain owned by Work API; Workplace Web still communicates only through HTTP and SignalR and gains no Console, storage, Runtime, provider, user, tenant, or authentication dependency.

## Consequences

Workplace and Work API can be built, tested, published, started, and health-checked independently. A transient Work API outage yields the Workplace error state rather than an in-process service fallback. SignalR reconnect explicitly restores group membership and HTTP remains authoritative. Local deployment now normally runs two Workplace-related processes, while Aspire additionally runs the Console as a separate optional operational resource.

This ADR supersedes only the single-process deployment choice in ADR-0020; its resource model and application boundaries remain accepted.
