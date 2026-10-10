# ADR-0168: Runtime execution is placed on independent AWP Workers

## Status

Accepted

## Context

Agentstration currently creates Microsoft Agent Framework (MAF) agents and workflows inside the authoritative server process. Runtime Runs and Flow Runs are dispatched through bounded in-memory queues, Flow execution uses a fixed optimistic document lease, and cancellation reaches active execution through process-local cancellation-token registries. This couples API availability and scaling to transient Runtime execution.

ADR-0032 established one authoritative standalone server to prevent competing data and control-plane authorities. That authority remains necessary, but it does not require transient MAF execution to share the authoritative process. Issue #656 introduces the Agentstration Worker Protocol (AWP) and independent Runtime Workers for that execution.

## Decision

### Authority and process boundary

Agentstration remains the single authority for durable Runtime Run and Flow Run state, immutable resource definitions, authorization, cancellation intent, assignments, attempts, leases, checkpoints, execution events, and terminal outcomes. Storage implementations remain private to the authoritative server.

Every MAF execution object, including transient `AIAgent` instances and active MAF workflow objects, is created and owned by an independent `Agentstration.Runtime.Worker.MicrosoftAgentFramework` process. The authoritative API process creates, controls, and observes durable Runs but does not construct MAF execution objects.

A Runtime Worker communicates with Agentstration exclusively through authenticated Agentstration APIs. It never receives an Agentstration database connection, concrete storage implementation, unrestricted resource repository, backend filesystem path, or backend credential. Model calls, governed Tool calls, checkpoint operations, child-Flow coordination, and Artifact access use assignment-scoped API operations until a later decision introduces another explicit data-plane boundary.

This decision supersedes ADR-0032 only where ADR-0032 places transient Runtime and Flow execution workers inside `Agentstration.Web`. ADR-0032 remains authoritative for the single durable API and data authority, the shared server-side stores, and the independently hosted Console and Workplace clients.

### AWP and placement unit

AWP is a versioned, transportable, provider-neutral pull protocol. It has no dependency on MAF, Entity Framework Core, SQLite, PostgreSQL, Kubernetes, an external broker, or agentgateway.

The placement unit is one complete executable Run: a direct Agent Runtime Run or a root Flow Run. An assignment identifies its target kind and durable Run identity. While an active MAF orchestration tree exists, its participants and active child Flows remain on the assigned Worker. Durable child Flow identities remain owned by Agentstration and are coordinated through assignment-scoped operations; they are not independently offered to another Worker while their parent orchestration remains active.

Workers register capabilities and capacity, wait for eligible work, atomically claim one complete Run, execute it, renew ownership, publish idempotent events, and report a terminal outcome. A long-poll wake-up is an optimization only. Durable Run and assignment state is the source of truth.

AWP protocol versions, Runtime capability versions, execution-material schema versions, and Worker implementation versions are distinct. A Worker registers the protocol and material versions it supports, the `microsoft-agent-framework` Runtime capability and its version, and its implementation/MAF version for diagnostics. Agentstration schedules by declared compatibility, so multiple Worker and MAF versions may coexist for the same Runtime family.

### Flow execution coordinates

Agentstration pins each assigned Flow Run to an immutable published Flow version and content hash. The Worker does not establish durable correlation by reporting free-form step names. It opens an effective step occurrence against a known `StepDefinitionId`, and Agentstration authorizes a `StepExecutionId`. Repeated or looped execution of one definition creates distinct StepExecution identities.

One Agent StepExecution may contain multiple logical Turns. Each Turn has a stable `TurnId` and contains one or more technical Turn Attempts. AWP v1 creates a server-authorized `TurnAttemptId` with `TurnAttemptNumber = 1` for every Turn and never creates attempt 2 automatically. Events and Tool calls carry their effective StepExecution, Turn, and TurnAttempt coordinates, a stable idempotency identity, and attempt-local ordering. Participants and their Turns remain subordinate to the owning StepExecution.

Controlled `WaitingForInput` suspension is distinct from Worker loss. It is entered only at a durable Turn boundary after the current TurnAttempt has completed and an explicitly versioned compatible checkpoint has been persisted. The Worker then relinquishes its AssignmentAttempt and capacity. Later input creates a new AssignmentAttempt and a new Turn under the same logical StepExecution; it does not create TurnAttempt 2 for the completed Turn. Loss during an active Turn remains a terminal worker-loss failure.

### Assignment, attempt, lease, and fencing

Every claim creates or activates a durable assignment attempt containing at least the assignment identity, attempt identity, Worker identity, Worker session identity, target Run, server-issued fencing generation, acquisition time, expiry time, and durable version. The Worker receives a cryptographically random opaque ownership token. Agentstration stores only a digest bound to the current assignment, attempt, Worker, session, fencing generation, and expiry. The token remains stable for one AssignmentAttempt; a new attempt or Worker session receives a new token and invalidates the prior ownership.

Claim is a storage-level atomic transition. Two Workers cannot acquire active ownership of the same placement unit. Heartbeat and lease renewal use server time and must match the current Worker, session, assignment, attempt, opaque ownership token, and fencing generation. Each assignment-scoped material read, checkpoint mutation, execution-event append, completion, and failure command validates that same ownership. Commands from an expired or superseded attempt are rejected and cannot mutate the Run.

The configured lease is materially longer than the heartbeat interval so bounded transient API outages do not immediately terminate healthy execution. Heartbeat runs independently from execution. The Worker may retry and buffer only bounded idempotent operations with stable event identities, stops starting new governed side effects when it cannot safely renew ownership, and receives no grace after server-observed expiry.

Cancellation is a durable server-owned intent. A heartbeat or another bounded assignment control response returns that intent to the Worker. A process-local cancellation token may implement cancellation inside the Worker, but it is never the authoritative cancellation record.

Terminal commands are conditional on the same durable assignment version and fencing state. The first valid durable terminal transition wins, except that cancellation intent already persisted before completion resolves the Run as cancelled rather than succeeded. Completion committed before a later cancellation remains completed. Expiry committed before completion rejects that completion. When cancellation was already persisted before expiry, the Run is cancelled and the AssignmentAttempt is interrupted rather than classified as `worker_lost`. Replaying the same terminal `EventId` is idempotent.

Lease expiry is detected by Agentstration without Worker cooperation. In this increment, Worker loss does not resume, reconstruct, or automatically retry an active MAF execution. The expired attempt becomes `Interrupted`, and its Run reaches an explicit terminal worker-loss failure. A caller may use the existing explicit retry behavior to create a new Run and assignment. Persisted checkpoints remain diagnostic and may support a separately decided future recovery policy, but their existence does not authorize automatic resumption in this epic.

### Worker identity

The Console BFF signed-request mechanism from ADR-0112 remains a Console-specific credential and authorization policy. It is not reused as the AWP Worker authentication scheme.

AWP introduces a distinct workload identity with authority limited to Worker registration, claim, and assignment-scoped operations. It grants no human identity, PAT authority, Management CRUD, generic Run enumeration, Secret retrieval, or Workspace membership. Server-side authorization binds every operation to the authenticated Worker and current assignment attempt. Credential provisioning, overlap during rotation, revocation, replay protection, and bounded security audit follow the same security properties as existing workload mechanisms without sharing their BFF-specific routes, claims, policies, or credential type.

Every Worker installation has a stable unique `WorkerId`; every process start creates a new `WorkerSessionId`. A new session supersedes the previous session for that Worker. Because this increment does not recover active MAF state, ownership is not transferred to the new session and the former session's active attempts follow the worker-loss policy.

Aspire and Compose provision a distinct protected credential file for each Worker identity. Manual installation uses an outbound-only, short-lived, single-use pairing code followed by explicit approval and protected persistence of the issued credential. Pairing codes are provided interactively, through standard input, or through a temporary protected file, never through a URL, ordinary command-line argument, or log. This enrollment follows AEP's security discipline without reusing the AEP protocol or identity type.

Low-level signing, nonce, clock-window, or protected-file utilities may be extracted into neutral security helpers only when they contain no BFF or AWP authorization semantics.

### Project and dependency boundary

`Agentstration.Runtime.Worker.MicrosoftAgentFramework` is a narrow, MAF-specific executable composition root. It may reference the autonomous AWP client, provider-neutral Runtime and Flow execution contracts, and `Agentstration.Runtime.MicrosoftAgentFramework`. It must not reference `Agentstration.Infrastructure`, any `*.Storage.*` project, Entity Framework Core, or a database provider.

AWP and the durable assignment model remain runtime-neutral. The Worker registers the explicit `microsoft-agent-framework` Runtime capability, and Agentstration offers it only compatible assignments. The wire-visible capability identity is independent from CLR project and namespace names. A future runtime implementation uses another executable and capability rather than adding provider selection or dynamic runtime loading to this Worker.

Worker-side adapters implement model, Tool, checkpoint, execution-material, effective Step/Turn identity, event, child-Flow coordination, and Artifact operations over AWP. The existing `Agentstration.Infrastructure` aggregation cannot be reused by the Worker because it composes MAF with concrete server stores. Neither `IResourceStore`, `IFlowRepository`, `AgentManagementService`, nor another generic repository is exposed over HTTP. MAF-specific construction and execution dependencies are moved behind narrow runtime contracts; server-owned storage and resource-resolution adapters remain in the authoritative server composition. After complete externalization, server composition no longer registers the in-process MAF Runtime/Flow executors.

### Standalone and development hosting

There is no in-process Worker fallback. A Worker is always a distinct operating-system process using the real AWP transport.

Aspire is the supported development orchestrator for the complete local topology. It starts the authoritative server and a configurable number `N` of identical Runtime Worker processes; the default development value is one. Each Worker has a distinct workload identity and capacity declaration. Increasing `N` changes only hosting configuration, not AWP or Run semantics.

Direct standalone startup uses a supported launcher or supervisor to start the same Worker executable as a separate process. Compose provides the equivalent explicit server-and-Worker resources. The executable may later be hosted by another process scheduler without changing AWP. Kubernetes, Kubernetes discovery, Pod identity, HPA, agentgateway, and a mandatory external broker remain outside this decision.

## Consequences

- API restart no longer destroys Worker-owned transient execution solely because the API process stopped, although the Worker cannot advance durable state while the authority is unavailable.
- Worker restart or loss terminates its active attempts explicitly; it does not resume them in this increment.
- Controlled input suspension may continue only from an explicitly persisted compatible checkpoint at a durable Turn boundary; this is not Worker-loss recovery.
- A logical Agent Step may span several Turns, while every Turn initially has exactly one TurnAttempt.
- SQLite remains the zero-dependency authoritative default, while SQLite and PostgreSQL must both implement the same atomic claim, renewal, fencing, expiry, and idempotency semantics.
- Existing Flow execution must be separated into a Worker-side execution engine and server-side durable state transitions instead of exposing `IFlowRepository` over HTTP.
- Existing direct Flow dependencies on `IResourceStore`, `AgentManagementService`, Flow repositories, model-provider resolution, Tool execution, and runtime-state stores must be replaced by immutable assignment material and narrow assignment-scoped operations.
- `Agentstration.Web` may retain hosted workers for control-plane maintenance, reconciliation, cleanup, recovery detection, and scheduling. Those services are not AWP Runtime Workers and cannot construct MAF execution objects.
- Architecture tests must forbid Worker dependencies on concrete storage, EF Core, database providers, and `Agentstration.Infrastructure`; forbid AWP abstractions from depending on MAF or hosting technologies; and forbid the authoritative API process from constructing MAF execution objects.
- A later decision is required before automatic retry, checkpoint-based resumption after Worker loss, distributed execution inside one active MAF tree, Kubernetes hosting integration, or direct Worker-to-provider credentials are introduced.
