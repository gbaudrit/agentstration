# ADR-0268: WorkPlan dispatch persists admission before root Flow submission

## Status

Accepted

## Context

Issues #946–#948 separate functional task hierarchy, reviewed acceptance and immutable WorkPlan intent. Issue #949 needs executable dispatch that survives a server restart and ambiguous submission failures. Work and Flow use separate stores, so a database transaction cannot span admission and root Flow creation. ADR-0168 forbids automatic recovery of a lost AWP Worker.

## Decision

An explicit execution command configures an active WorkPlan with exact published Flow targets and bounded inputs. Activation alone still never executes work. The Work Plane scheduler checks current readiness and authorization, captures the plan revision/digest, task version/creation digest, accepted dependency versions and any admission-gate evidence, and commits an attempt intent together with the task's queued state and command receipt in one Work-store transaction. Task version guards prevent competing plans from admitting the same task.

Each attempt owns stable WorkItem and root Flow Run identities. Submission uses `RootFlowSubmissionService` and `IWorkExecutionGateway`; replay repairs an interrupted queue handoff and ensures the same root Run. A separate execution WorkItem retains technical execution history while the functional task keeps its hierarchy and acceptance. The attempt is an outbox intent, not a second runtime assignment or a worker lease. Its exact Flow target and captured evidence never change after admission.

The control-plane scheduler reads persisted Flow outcomes through a narrow authorized observer. These are the durable projection of Flow completion events; polling repairs a missed in-memory notification or a crash before the Work-store commit. Terminal outcome projection and the functional task's execution state are committed together. A successful Flow does not accept a task. Existing reviewed evidence and composite consolidation remain authoritative for dependencies and parent outcomes.

Cancellation persists disabled admission and cancellation intent before contacting Flow. It is asynchronous and may race with already admitted execution. Explicit retry of a failed/cancelled technical execution requires pending functional acceptance and creates a new attempt; it retains the old attempt. Final reviewed functional outcomes remain final under ADR-0266. A worker-loss failure is terminal, never automatically retried or resumed.

Admission gates share this scheduler and may deny dispatch or supply bounded, versioned evidence. Generic dispatch needs no external provider or gate. Issue #1346 owns repository dependency observations and their freshness/authorization; their future evolution must not rewrite admitted attempts.

Attempt/configuration data extend the existing versioned WorkPlan JSON snapshot. No relational columns change, so existing SQLite initialization and PostgreSQL migrations remain applicable. Both stores implement the same transactional task-update/version guards. For execution-inclusive plan heads, the authoring/control version limit becomes 1,024, superseding ADR-0267's 256-command bound; at most 200 attempts are retained. History and input bounds cap retained state; already admitted completion and cancellation remain possible at the authoring history limit. Queue recovery identities include exact execution scope, Principal and WorkItem, preventing collisions when a portable WorkItem identity is reused in another Workspace.

## Consequences

- Submission uncertainty is recovered by identity, without claiming atomic transactions across Work and Flow stores.
- The authoritative server can restart before or after root Run creation without creating another attempt or root Run.
- Flow and AWP retain execution, cancellation and worker-loss authority. Work owns readiness and functional acceptance.
- The scheduler revalidates the stored Principal's current execution permission and exact Workspace before advancing. Revocation prevents further work and exposes a bounded diagnostic; it never impersonates a different owner.
- No UI, automatic acceptance, automatic functional reopening, repository integration or external sharing is introduced by this slice.
