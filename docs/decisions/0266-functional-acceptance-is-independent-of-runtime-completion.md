# ADR-0266: Functional acceptance consolidates independently of runtime completion

## Status

Proposed — implementation for [#947](https://github.com/gbaudrit/agentstration/issues/947).

## Context

The durable functional trees in ADR-0265 reuse WorkItem identities. A successful Runtime or Flow execution is evidence for review, but does not establish that the functional objective is accepted. Required and optional children need deterministic consolidation, durable evidence and retry-safe updates without another execution authority.

## Decision

- Preserve execution `Status`, `Result` and runtime events. Add a separate nullable functional acceptance snapshot. An absent acceptance means Pending; runtime completion alone never changes it.
- A caller with execution permission may record one explicit terminal functional outcome for an atomic tree node: Accepted, Failed or Cancelled. Require a stable event UUID, the expected node version and nonempty evidence bounded to 8,000 characters. The server supplies the principal, Workspace and time. The outcome is immutable in this increment; reopening requires a future explicit command.
- Default children to required. Persist `IsOptional` in the immutable hierarchy. Omit its default false value from the canonical definition JSON so the #946 creation digest and replay contract remain compatible with existing trees.
- Derive composite acceptance solely from direct required children. Any Failed child produces Failed; otherwise any Cancelled child produces Cancelled; otherwise a nonempty set of all Accepted required children produces Accepted. All other cases, including empty composites and optional-only composites, remain Pending. Optional child outcomes remain available as evidence but do not condition the parent outcome.
- Persist the atomic evidence receipt and each ancestor's required-child references, outcomes and observed versions. Keep the leaf evidence on its original node rather than copying potentially sensitive evidence into every ancestor. Tree and subtree reads expose both functional acceptance and execution status.
- Commit the changed leaf and all ancestors in one Work-store transaction with optimistic versions. Every new outcome advances the tree root version, including outcomes under optional branches, so concurrent functional writes share a guard. On a conflict, reload and retry at most three attempts. A stale target version remains a conflict.
- Replay the same event, normalized evidence and outcome without advancing any version or appending history. Reusing an event identifier for another node or another intent is a conflict. Receipts survive restart and are bounded by the immutable tree size: one per atomic node.
- Expose `POST /api/workspaces/{workspaceName}/tasks/{taskId}/outcome` as a synchronous command returning the current complete family. Require the existing contextual execution policy; impose a 64 KiB request-body limit. Direct attempts to accept a composite are rejected because its result is derived.

## Consequences

SQLite and optional PostgreSQL use the same version and transaction contract. New state is contained in the existing JSON snapshot, so no relational schema migration is needed for this increment. Older snapshots deserialize with required children and no functional acceptance.

This increment performs no dispatch, no external issue mutation and no automatic issue closure. WorkPlan execution remains #949 and repository issue/dependency authority remains #1346. Functional acceptance does not change Task supervision's existing execution-status projection. Reopening, alternative acceptance policies and new UI journeys require separately governed commands and remain outside this increment.
