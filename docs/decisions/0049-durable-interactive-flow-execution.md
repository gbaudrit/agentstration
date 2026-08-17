# ADR-0049 — Durable interactive Flow execution preserves exact runtime identity

- Status: Accepted
- Date: 2026-08-17

## Context

An orchestration can pause for external input after Microsoft Agent Framework (MAF) has already selected agent revisions and created internal workflow state. Rebuilding from the latest agent definition, keeping a checkpoint only in memory, or treating the browser as the owner of the pending question would make a restart change the execution or lose it entirely.

MAF 1.16 exposes JSON checkpoint persistence through `CheckpointManager`, `ICheckpointStore<JsonElement>`, `RunStreamingAsync`, and `ResumeStreamingAsync`. A restored run re-emits pending `RequestInfoEvent` values. MAF's non-autonomous handoff builder does not itself create a `RequestInfoEvent`; it ends the current caller turn. `RequestInfoEvent` handling therefore applies to MAF workflows that declare an external `RequestPort`.

## Decision

`FlowRun.DefinitionSnapshot` remains the immutable source of Flow behavior. On first materialization, the runtime records an immutable `RuntimeExecutionBinding` for every participant: agent namespace and resource ID, generation, revision, deployment, runtime profile, and model profile. Resume resolves these exact identities and never calls latest-resolution.

Runtime checkpoint bytes are opaque outside the adapter. `IRuntimeExecutionStateStore` is a runtime-neutral port, with local SQLite persistence keyed by run, runtime type, and state ID. The MAF adapter owns JSON checkpoint interpretation and stores parent checkpoint links.

External interaction is a Flow-owned durable state machine:

- a runtime request creates one `InputRequest` and transitions the run to `WaitingForInput`;
- a response records value, time, and principal through optimistic concurrency;
- duplicate responses conflict and do not resume twice;
- accepted responses enqueue continuation and return HTTP `202`;
- expiry marks the request expired and the run timed out;
- startup and periodic recovery re-enqueue pending, lease-expired, and answered-but-not-resumed runs.

The Workplace `PendingAction` is a presentation projection, not a second source of truth. It carries the Flow run and input-request identities, moves the Work item to action-required, and delegates its response to the same `FlowRunService`. Projection is idempotent and repaired during recovery.

Dispatch is at-least-once. An optimistic execution lease allows only one worker to claim a run, and the lease duration must exceed the orchestration timeout. Side effects remain responsible for stable operation identifiers and provider-level idempotency as those integrations are added.

Agent revisions are retained by policy (latest three and younger than 30 days by default) and by live run references. Purge impact separates active, waiting, and historical references. Normal purge is blocked by policy or active use. Forced purge first terminates affected runs with an explicit error, stops and reconciles the deployment, deletes the deployment and revision, and appends a Management audit event.

## Consequences

- Process restart does not change participant generations or require in-memory workflow objects.
- Flow, REST, Console, and Workplace observe the same pending-input record.
- SQLite remains the standalone default; no external broker or Durable Task dependency is introduced.
- Historical runs retain diagnostic binding metadata even after an eligible revision is purged, but cannot be resumed.
- A force purge is intentionally disruptive, explicit, and auditable.
