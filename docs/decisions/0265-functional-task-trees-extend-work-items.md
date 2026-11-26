# ADR-0265: Functional task trees extend existing Work Items

## Status

Proposed — implementation for [#946](https://github.com/gbaudrit/agentstration/issues/946).

## Context

WorkTask is the public identity and projection of an anchor WorkItem. Entry and Trigger work already use that identity, while continuations attach further executions through `workplace.taskId`. Functional decomposition needs parent/child relationships independent of technical continuation and Flow Run ancestry. Creating a competing task store would split ownership, authorization and history.

## Decision

- Extend the canonical WorkItem snapshot with optional immutable task hierarchy: tree identity, atomic/composite kind, functional parent and creation digest. Existing snapshots omit this field and retain their behavior and identities.
- Create an entire bounded tree through one Work application service and one atomic storage operation. The caller supplies stable task identifiers; a canonical digest identifies the complete normalized creation intent. Replaying the same identity and intent returns the existing tree. A different intent or a colliding task identity is a conflict, and no subset of the tree is committed.
- Derive Workspace and principal ownership from the authenticated context. Parent references must resolve inside the submitted tree, so this first slice cannot adopt an existing or foreign-owned task. Store reads include Workspace and principal. No principal override is exposed in the HTTP contract.
- Persist tree identity as an indexed Work-owned query column alongside the canonical snapshot. SQLite upgrades the projection during initialization; optional PostgreSQL uses a migration. Both adapters preserve atomic creation and bounded reads.
- Limit creation to 100 tasks, root-relative depth eight, 100,000 instruction characters per task and 1,000,000 aggregate text characters. The HTTP creation body is bounded to 8 MiB. Retrieval is bounded by family size and depth and supports descendant subtrees.
- Expose synchronous creation at `POST /api/workspaces/{workspaceName}/tasks/tree` and subtree reads at `GET /api/workspaces/{workspaceName}/tasks/{taskId}/tree`. New functional tasks use the existing supervision projection, retain their WorkTask/WorkItem identity, and begin Pending with no execution requested.
- Membership and task kind are immutable in this increment. A retry cannot convert an existing atomic task into a composite. Existing single-Task deletion rejects tree members to preserve parentage until tree-aware lifecycle/deletion is implemented.
- Return `Result<T>` with invariant error codes for expected creation/read failures. Infrastructure failures propagate; uniqueness conflicts alone are recovered as idempotency/conflict outcomes.

## Consequences

Generic task trees work with SQLite, without Git, an external tracker or a model. There is no new scheduler, resource family or execution authority. Future shared references map to these local identities rather than copying local database or Workspace identities between instances.

This increment does not execute tasks, aggregate composite completion, introduce WorkPlans or expose a new UI. Those capabilities remain owned by #947, #948/#949 and #927. Runtime and Flow ancestry and continuation metadata keep their existing meaning. ADR-0023 and principal ownership remain authoritative for supervision and access.

The initial API creates new trees and reads existing subtrees. Editing, reparenting, linking already existing tasks and tree-aware deletion require separate commands with explicit concurrency and lifecycle rules; they are not inferred from a replayed create request.
