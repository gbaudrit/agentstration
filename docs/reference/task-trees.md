# Functional task trees

The Work API supports durable atomic and composite tasks independently of an Entry, Git repository, external issue tracker or model. A functional task retains the existing WorkTask/WorkItem identity. Its hierarchy is separate from Flow execution ancestry and Task continuations.

## Create a tree

Send `POST /api/workspaces/{workspaceName}/tasks/tree` with the run-submission permission in the current authorized Workspace. Every task has a caller-supplied stable UUID. Supply exactly one root and include every referenced parent in the request.

```json
{
  "rootTaskId": "10000000-0000-0000-0000-000000000001",
  "tasks": [
    {
      "id": "10000000-0000-0000-0000-000000000001",
      "kind": 1,
      "title": "Assessment",
      "instruction": "Assess the collected evidence"
    },
    {
      "id": "10000000-0000-0000-0000-000000000002",
      "kind": 0,
      "parentTaskId": "10000000-0000-0000-0000-000000000001",
      "title": "Collect",
      "instruction": "Collect evidence"
    }
  ]
}
```

`kind` is the invariant enum `Atomic = 0`, `Composite = 1`. Only composites can have children. Children are required by default; set `isOptional: true` when a child's outcome must not condition its parent. This flag is immutable. Empty composites remain Pending; creation does not imply successful completion. Titles, instructions and descriptions are user-authored content.

Successful creation returns `201 Created`, a `Location` for the tree and the stored nodes with kind, parent, status, version and timestamps. Replaying the same tree identifiers and normalized content also returns `201` with the existing state, without resetting timestamps, creating duplicate tasks or requesting execution. Task order in the request does not affect replay. Changing the original intent under the same identity returns `409 task_tree_conflict`.

Creation is atomic: a collision on any task rolls back the whole new family. The request cannot attach an existing task or reference a parent outside the submitted tree. Caller ownership comes from the authenticated principal; no ownership override is accepted.

## Read a tree or subtree

Use `GET /api/workspaces/{workspaceName}/tasks/{taskId}/tree` with run-read permission. A root returns its family; a descendant returns only itself and its descendants. Reads are restricted to the current Workspace and principal. Missing or inaccessible roots return `404 work_task_not_found`.

Existing Entry/Trigger tasks can be read as atomic single-node trees. New task nodes also participate in existing task supervision; technical Flow-only WorkItems remain excluded from Workplace task classification.

## Record and consolidate functional outcomes

Send `POST /api/workspaces/{workspaceName}/tasks/{taskId}/outcome` with execution permission, using the current `version` of an atomic node in a created tree. Legacy Entry/Trigger projections retain their existing lifecycle:

```json
{
  "eventId": "20000000-0000-0000-0000-000000000001",
  "expectedVersion": 1,
  "outcome": 1,
  "evidence": "Reviewed the result against the task's acceptance criteria."
}
```

`outcome` is the invariant enum `Pending = 0`, `Accepted = 1`, `Failed = 2`, `Cancelled = 3`. Only terminal outcomes may be submitted. Evidence is required and bounded to 8,000 characters. The authenticated user makes an explicit functional judgment; a completed Runtime or Flow execution alone never supplies acceptance. Conversely, a task can be accepted from reviewed evidence without a local execution. Recording acceptance preserves execution `status` and runtime results.

Success returns `200 OK` with the current complete tree. Each node exposes `isOptional` and nullable `acceptance`; absent acceptance means Pending. Atomic acceptance contains the stable event identifier, outcome, normalized evidence and server timestamp. Composite acceptance contains direct required-child IDs, functional outcomes and observed versions, allowing evidence to be traced back through the tree.

Consolidation proceeds from the changed node to the root. A required Failed child makes its parent Failed; otherwise a required Cancelled child makes it Cancelled. A nonempty set of all Accepted required children makes the parent Accepted. Otherwise the parent remains Pending. Optional children do not affect that decision. Empty and optional-only composites remain Pending. Composite outcomes are derived, so posting directly to a composite returns `409 task_outcome_derived`.

All changed nodes are saved atomically with optimistic concurrency and a root version guard. A stale target version returns `409 task_tree_conflict`; retry after rereading the task. Concurrent updates to different children are reloaded and retried up to three attempts. Replaying the same event, outcome and trimmed evidence returns current state without changing versions or history, even when its original expected version is now stale. Reusing an event within the same tree for another task or another intent returns `409 task_outcome_conflict`.

An atomic outcome is final in this increment. A second event cannot replace the existing evidence or reopen a failed/cancelled task. Reopening requires a future explicit lifecycle command. The event and evidence receipt survive restart, independently of execution history. HTTP outcome bodies are limited to 64 KiB.

## Bounds and failures

- At most 100 tasks and eight parent edges from the root.
- Titles are required and limited to 512 characters; instructions are required and limited to 100,000 characters; descriptions are limited to 2,000 characters.
- Aggregate title/instruction/description text is limited to 1,000,000 characters; HTTP creation bodies are limited to 8 MiB.
- Duplicate identifiers, cycles, multiple roots, unknown parents, invalid kinds and atomic parents return Problem Details with invariant validation codes and status `400`.
- Tree membership and kind are immutable. Existing single-Task deletion returns `409 task_tree_delete_not_supported` for a tree member, preserving its family.

## Persistence and current scope

Work-owned SQLite stores the hierarchy in the WorkItem snapshot and indexes the tree identity by Workspace and principal. Initialization adds the nullable projection/index to existing stores. PostgreSQL has the equivalent migration and adapter behavior; live PostgreSQL validation is opt-in.

Creation and reading are the #946 slice; evidence-based functional consolidation is #947. [WorkPlan revisions, dependency readiness and durable execution](work-plans.md) are #948–#949. Shared-project synchronization and the Workplace authoring journey are subsequent capabilities. PostgreSQL adapter compilation is verified; live PostgreSQL execution remains opt-in. See [ADR-0256](../decisions/0256-functional-task-trees-extend-work-items.md) and [ADR-0257](../decisions/0257-functional-acceptance-is-independent-of-runtime-completion.md).

## Task FlowRun history

Operational Task histories query the exact Tenant, execution Workspace, Principal and
`WorkTaskId` before paging. `GET /api/tasks/{taskId}/flow-runs/page?skip=0&take=25`
returns `items`, `skip`, `take`, `hasMore` and `totalCount`; `take` must be 1–200 and
`skip` non-negative. Pages sort by immutable run creation time, then run identity.
Updating an execution status cannot move a run across page boundaries. As with
offset-based collections, concurrent insertions or deletions require callers to
refresh the traversal; the count describes the current query, not a frozen snapshot.

The existing complete history and Task detail responses retain their shapes and
read all Task-specific pages. Summary counts use a scoped database count without
loading FlowRun payloads. Unrelated Tasks cannot truncate either result. Existing
SQLite databases are upgraded idempotently at initialization; the optional
PostgreSQL store includes an equivalent migration and index. Both preserve run
payloads, ETags and update timestamps.
