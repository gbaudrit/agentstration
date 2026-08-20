# ADR-0062: Triggers submit Work through a reconstructible Quartz projection

Status: Accepted — 2026-08-20

## Context

Agentstration must start work without an active user request. The platform already has one execution chain: `WorkItem` resolves an immutable `FlowReference`, creates a `FlowRun`, and delegates technical execution to Runtime. The historical `MissionSchedulerWorker` is a 30-second `PeriodicTimer` poller over the legacy content-monitoring `Mission.NextRunAt`. It invokes `MissionService` directly, does not create Work or FlowRuns, has no durable occurrence identity, misfire policy, execution identity, or scheduler recovery, and therefore cannot be the foundation for proactive Work.

## Decision

`Trigger` is a Workspace-scoped Management resource using the canonical `apiVersion` / `kind` / `metadata` / `definition` envelope, ETag concurrency, namespace, generation, desired definition and separate observed status.

The ownership rule is:

```text
Trigger owns when
Work owns what
Flow owns how
Runtime owns execution
```

There is no Automation Runtime or second execution engine. “Automation” may later be a Workplace projection over a Trigger, its target and its Work history.

V1 implements only `source.kind: schedule`, with:

- `once` and an offset-bearing instant;
- Quartz cron expressions plus an explicit IANA time zone;
- ISO-8601 intervals with an explicit `startAt` anchor.

The source discriminator remains extensible for future webhook, event and condition sources. V1 targets a Flow directly. Entry is deliberately not required because its contract and presentation belong to Workplace users. At each firing, an active target is resolved and persisted on Work as an exact, immutable Flow version.

Management resources are the source of truth. Quartz.NET 3.19 uses a local Microsoft SQLite ADO JobStore only as a runtime projection. Create, update, enable, disable and delete reconcile the projection, and startup rebuilds every registration from Trigger resources. Quartz types do not appear in Management or Work contracts.

Every scheduled firing has a deterministic occurrence ID derived from Workspace, Trigger UID and UTC scheduled instant. The occurrence is inserted durably before Work submission. The WorkItem uses the same deterministic GUID; a retry can recover the Work correlation after a crash between Work creation and occurrence completion. This provides **at most one local Work submission per Trigger occurrence**. It does not provide exactly-once Flow or tool effects.

Manual `Run now` creates a distinct manual occurrence, uses the same authorization, target-resolution and Work-submission pipeline, rejects disabled Triggers, and does not change the schedule.

Misfires are explicit and bounded:

- `skip` advances without submitting missed occurrences;
- `fireOnce` collapses missed time into one immediate firing.

There is no unbounded catch-up backlog. Concurrency is also explicit: `skip` does not submit when prior Work from the Trigger is active; `allow` permits overlap. Queueing is not implemented in V1.

Enabling or changing an enabled Trigger snapshots a server-owned execution scope containing Tenant, Workspace and owner Principal IDs. Every future firing reloads the Principal and Workspace and re-evaluates current `runs/execute` authorization before submission. A disabled Principal, Workspace, removed membership or revoked permission fails closed. No null Principal or RBAC bypass is permitted.

Autonomous Work is projected as a Task without an Entry or Interaction. If its Flow requests input, the existing durable PendingAction mechanism is reused with a Task link and Workplace notification; no synthetic user message or Interaction is created.

The legacy `MissionSchedulerWorker` is removed from startup. Missions remain an explicitly legacy, manually executable content-monitoring vertical until that vertical is migrated or removed. This ADR supersedes ADR-0004 for new Work scheduling.

## Consequences and limits

- Agentstration remains a single deployable process and V1 runs one logical scheduler instance.
- SQLite locking and Quartz recovery support local restart; clustering is not enabled.
- Trigger history contains scheduling/submission facts and Work correlation only. Flow and Runtime diagnostics remain in their owning records.
- Static JSON input and occurrence metadata are supported. Secrets remain references in providers/tools; raw credentials are rejected by design rather than embedded in Trigger input.
- Notifications are emitted for required human action. Completion/failure notification policies are future configuration, not unconditional V1 behavior.
- Packs do not install active Triggers in this increment. A future Trigger Pack handler must force installed Triggers disabled and require explicit configuration/activation.
- Webhooks, external events, conditions, queue concurrency, distributed scheduling and workload identities remain out of scope.
