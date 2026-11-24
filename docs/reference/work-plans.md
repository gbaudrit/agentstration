# WorkPlans

A WorkPlan records an intent, explicit task membership and prerequisite edges in the Work Plane. Its graph is distinct from [task hierarchy and acceptance](task-trees.md), Flow execution composition and Resource Planning proposals. Creation, validation and activation are synchronous data operations; they do not start an agent, Flow or Runtime Run.

Plans are private to their Workspace and authenticated Principal. Requests cannot select another owner. Create the functional tasks through the task-tree API first; legacy Entry/Trigger task projections retain their existing lifecycle and are not adopted by this increment.

## Create a plan

Send `POST /api/workspaces/{workspaceName}/work-plans` with run-submission permission. Supply stable UUIDs for the plan and command:

```json
{
  "id": "30000000-0000-0000-0000-000000000001",
  "commandId": "40000000-0000-0000-0000-000000000001",
  "definition": {
    "intent": "Collect evidence before writing the summary.",
    "taskIds": [
      "10000000-0000-0000-0000-000000000002",
      "10000000-0000-0000-0000-000000000003"
    ],
    "dependencies": [
      {
        "taskId": "10000000-0000-0000-0000-000000000003",
        "dependsOnTaskId": "10000000-0000-0000-0000-000000000002"
      }
    ]
  }
}
```

Both referenced tasks must already exist in the caller's Workspace and belong to the caller. They may belong to different functional trees. Every dependency endpoint must be a member of this plan. Duplicate membership, duplicate edges, self-dependencies and cycles are rejected.

Success returns `201 Created`, a `Location` for the plan, lifecycle `status`, optimistic `version`, `currentRevision`, the immutable revision and current task readiness. A plan starts Draft. Replaying the same command and normalized intent returns the current plan without creating another revision or resetting state.

A selected composite is a derived checkpoint, not a dispatchable task. All its direct required children must also be plan members; the rule applies recursively to selected composites. Optional children can remain outside the plan. Validation checks the combined completion constraints for cycles: a child cannot wait on a parent that requires that same child to finish. The stored `dependencies` remain exactly the explicit plan edges.

## Validate and activate an exact revision

Read `GET /api/workspaces/{workspaceName}/work-plans/{planId}` with run-read permission. Copy its current `version`, revision number and revision `digest` into a lifecycle command:

```json
{
  "commandId": "40000000-0000-0000-0000-000000000002",
  "expectedVersion": 1,
  "revision": 1,
  "revisionDigest": "<the exact digest returned by the API>"
}
```

Send it to `POST .../work-plans/{planId}/validate` with execution permission. Success returns `200 OK`, status Validated and a new plan version. Validation records the exact revision number and digest. Then send another command with a new command ID and the returned version to `POST .../work-plans/{planId}/activate`.

Activation returns Active, pins the validated revision and exposes Ready/Blocked states. It does not dispatch work. A stale version, unvalidated revision or different revision pin returns `409`. Activation is rejected while any member has active local execution.

The invariant status values are `Draft = 0`, `Validated = 1`, `Active = 2`, `Inactive = 3`. A plan whose tasks are accepted remains Active until explicitly deactivated; automatic execution/lifecycle advancement belongs to #949.

## Readiness and dependency satisfaction

Readiness is computed from the immutable plan revision and current owned tasks. It is advisory and includes each observed task version; a later executor must claim work atomically.

- A task with functional acceptance is Accepted and needs no dispatch.
- A pending atomic task in an Active plan is Ready only when all explicit prerequisites have functional acceptance and its local execution is still Pending.
- A composite remains a derived checkpoint until its required children supply acceptance.
- A completed local execution without functional acceptance does not unlock downstream work. Failed/cancelled functional outcomes also do not satisfy prerequisites.

The invariant readiness values are `Ready = 0`, `Blocked = 1`, `Accepted = 2`. Each task includes `reasons` and `unmetDependencies`:

| Reason | Meaning |
| --- | --- |
| `plan_not_active` | The plan has not been activated or is inactive. |
| `dependencies_not_accepted` | One or more explicit prerequisites lack functional acceptance. |
| `composite_outcome_derived` | The task's acceptance is derived from its required children. |
| `task_active` | Local execution is Queued, Running, waiting for input/approval, or Paused. |
| `task_review_required` | Local execution is terminal, but functional acceptance is still pending. |
| `task_outcome_terminal` | The functional outcome is Failed or Cancelled. |
| `task_unavailable` | The owned task is no longer available. |
| `task_intent_changed` | The referenced creation intent no longer matches the pinned revision. |

## Revise an inactive plan

Use `POST .../work-plans/{planId}/deactivate` with a lifecycle command pinned to the current version and revision. Deactivation preserves the revision and its validation pin. It is refused while member execution is active; it does not cancel tasks.

Send `POST .../work-plans/{planId}/revisions` with execution permission:

```json
{
  "commandId": "40000000-0000-0000-0000-000000000003",
  "expectedVersion": 4,
  "definition": {
    "intent": "Revised remaining objective.",
    "taskIds": ["10000000-0000-0000-0000-000000000003"],
    "dependencies": []
  }
}
```

Success returns `201 Created` and a `Location` for the new immutable revision. Draft, Validated and Inactive plans can be revised; Active plans cannot. A revision checks both old and new members, including removed tasks, and refuses the change if any of them has active execution. A new revision returns to Draft and clears validation. It never changes a task's definition, execution, acceptance, version or history.

Read an exact prior definition through `GET .../work-plans/{planId}/revisions/{revision}`. It retains intent, membership, explicit edges, task creation-intent references, digest and creation time. Revision numbers are monotonic and old rows are not rewritten, even when a new revision deliberately repeats the same content.

## Receipts, storage and portability

Every accepted command has an immutable receipt available at `GET .../work-plans/{planId}/commands/{commandId}`. It records the action, applied plan version, exact revision reference, server time and observed task versions. Command identity is scoped to the owned plan. Reusing it with another semantic intent returns `409 work_plan_command_conflict`. Expected versions are concurrency conditions, not part of the semantic input digest, so exact retries remain possible after later changes.

The store commits the plan head, new revision when applicable and receipt atomically. It locks and checks the observed tasks in the same transaction; planning does not advance task versions or alter task history. A task starting after preflight causes a conflict instead of allowing an unsafe revision. A failed write leaves no partial head, revision, receipt or task changes.

SQLite stores these documents in the Work-owned `WorkPlans`, `WorkPlanRevisions` and `WorkPlanCommands` tables. Initialization adds missing tables without replacing tasks. Optional PostgreSQL has the equivalent migration and transaction contract. Live PostgreSQL checks remain an opt-in workload.

The caller-supplied plan ID and `(revision number, digest)` retain a portable intent. Its digest covers normalized intent, sorted task IDs with their creation-intent digests and sorted dependencies. It excludes local Workspace/Principal identities, timestamps and mutable execution/acceptance state. The later Project sharing adapter owns serialization, transport and identity mapping; exporting local database rows is not a sharing protocol.

## Bounds and current scope

Plans are limited to 100 tasks, 1,000 explicit edges, 8,000 intent characters, 100 revisions and 256 commands. Existing receipts remain replayable at the history limit. Definition HTTP bodies are limited to 256 KiB and lifecycle bodies to 4 KiB. Invalid graph/input failures use Problem Details `400`; inaccessible records use `404` after authorization; concurrency, active-work, history-limit and lifecycle conflicts use `409`.

This is #948. Automatic dispatch and execution admission are #949; Project sharing is #1345. In issue-realization mode, #1346 owns the one-issue/one-plan association and repository-owned issue acknowledgement, hierarchy and dependencies. Those external relationships are not copied into this internal dependency graph. See [ADR-0258](../decisions/0258-workplans-pin-revisions-and-coordinate-readiness.md).
