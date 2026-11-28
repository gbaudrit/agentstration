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

## Declare portable task connection names

An optional `definition.taskConnections` array associates an atomic member task with the
logical Project needs it consumes: `{ "taskId": "<uuid>", "names": ["backend-tracking"] }`.
Entries and names are unique and normalized; they participate in immutable revision identity.
This contract is shared by REST, Work planning MCP and authored/imported YAML plans. Portable
YAML uses the existing typed `{ value: <uuid> }` task identity representation.

These declarations contain no local Flow, input or service assignment. At execution start,
omitted local target selections use the declaration. An explicit different selection fails;
consuming any need requires a Project and its locally assigned compatible ConnectedService.
[Project sharing](project-sharing.md#portable-named-connection-needs) additionally validates
shared declarations and target identity before new admission. Empty declarations keep ordinary
local plans provider-optional.

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

The invariant status values are `Draft = 0`, `Validated = 1`, `Active = 2`, `Inactive = 3`. A plan whose tasks are accepted remains Active until explicitly deactivated. Explicit execution configuration enables the durable scheduler described below.

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

Plans are limited to 100 tasks, 1,000 explicit edges, 8,000 intent characters, 100 revisions and 1,024 authoring/control versions. Existing receipts remain replayable at the history limit. Already admitted completion and cancellation can still progress at that limit. Execution retains at most 200 attempts. Definition HTTP bodies are limited to 256 KiB and lifecycle bodies to 4 KiB. Invalid graph/input failures use Problem Details `400`; inaccessible records use `404` after authorization; concurrency, active-work, history-limit and lifecycle conflicts use `409`.

Revisions/readiness are #948 and durable execution is #949; Project sharing remains #1345. In issue-realization mode, #1346 owns the one-issue/one-plan association and repository-owned issue acknowledgement, hierarchy and dependencies. Those external relationships are not copied into this internal dependency graph. See [ADR-0300](../decisions/0300-workplans-pin-revisions-and-coordinate-readiness.md) and [ADR-0301](../decisions/0301-workplan-dispatch-persists-admission-before-root-flow-submission.md).

## Execute an active plan

Send `POST .../work-plans/{planId}/execution` with execution permission, the current plan version and validated revision, and one Flow target for every atomic member:

```json
{
  "commandId": "50000000-0000-0000-0000-000000000001",
  "expectedVersion": 3,
  "revision": 1,
  "revisionDigest": "<digest returned by the plan>",
  "targets": [
    {
      "taskId": "10000000-0000-0000-0000-000000000002",
      "flow": { "flowId": { "value": "collect" }, "useActiveVersion": true },
      "input": { "instruction": "Collect evidence" }
    },
    {
      "taskId": "10000000-0000-0000-0000-000000000003",
      "flow": { "flowId": { "value": "summarize" }, "useActiveVersion": true },
      "input": { "instruction": "Summarize reviewed evidence" }
    }
  ]
}
```

The response is `202 Accepted` with a plan `Location`. Targets resolve to exact published versions and are checked again at admission. Inputs must be JSON objects, at most 64 KiB each and 256 KiB combined; request bodies are limited to 1 MiB. Inputs are explicit: prerequisite outputs are retained as evidence and are not implicitly interpolated into another task's input.

An optional `projectId` selects an associated private Project. Each execution target can list `connections: [name]` to consume named Project requirements; omitted lists consume none. An unknown or unconfigured selected name fails explicitly, while unrelated unconfigured needs do not block local execution. Its authorized exact [context references](projects.md#capture-context-for-execution) are captured independently for each admitted attempt; later Project edits never rewrite an existing attempt. Omit it for generic standalone execution.

The hosted control-plane scheduler checks configured plans every two seconds in bounded pages. `POST .../execution/advance` also runs the same service for deterministic administration. Every attempt exposes its immutable plan/task pins, dependency versions, admission evidence, execution WorkItem ID, root Flow Run ID, lifecycle status and bounded diagnostic code. Admission and its task claim commit before submission; a failed/uncertain handoff reuses these exact identities on restart. Completed/failed/cancelled Flow state is projected durably into the attempt and task. It never substitutes for reviewed functional acceptance.

Use `POST .../execution/cancel` with `{ "commandId": "<uuid>", "expectedVersion": 6 }` to stop future admissions and persist cancellation of open attempts. Flow cancellation is asynchronous; a completion committed before cancellation stays completed. Use `POST .../execution/retry` with the same fields plus `taskId` to explicitly retry a failed/cancelled technical execution whose functional outcome is still Pending. Retry creates a new attempt and keeps earlier evidence. Replaying an identical command is safe; changing its intent conflicts. A final reviewed Accepted/Failed/Cancelled outcome is not reopened by technical retry.

Execution commands, observations and recovery are private to the original Workspace/Principal and revalidate current execution access. Worker loss produces a failed task attempt and leaves successors blocked until an explicit retry and acceptance. No automatic AWP recovery is added. Deactivate and revise only after active tasks settle; a new revision clears execution configuration and retains past attempts.

## Governed MCP planning

The internal MCP broker publishes these Workspace operations:

| Tool | Operation |
| --- | --- |
| `work.project.list` / `work.project.get` | Private Project pages (up to 100) or exact lookup |
| `work.task.get` / `work.task.create-tree` | Read or idempotently create a functional task tree |
| `work.task.record-outcome` | Explicitly review a task with evidence and expected version |
| `work.plan.get` / `work.plan.propose` | Read or create a Draft plan |
| `work.plan.revise` | Revise an inactive plan using its expected version |
| `work.plan.validate` / `work.plan.activate` | Transition an exact revision number and digest |
| `work.plan.start` / `work.plan.advance` | Configure and advance the durable execution path |
| `work.tracking.describe` / `work.tracking.get` | Discover optional tracking capabilities or read a private operation receipt |
| `work.tracking.begin` / `work.tracking.resume` | Use the same tracking application service with a stable trusted Tool call identity |

Schemas expose the same definition and execution-target shapes as the REST contracts.
MCP WorkPlan results contain `commandId` and `value`, where `value` is the same
`WorkPlanResponse` as REST. Model input contains no Workspace, Principal, command ID or
causal coordinates. A stable MCP `agentstration/idempotencyKey` in request metadata
identifies retries; retain the same target plan or task IDs. Different intent under
an existing resource command receipt fails with the canonical conflict code.

Assign the Workspace Tool resource to an Agent using the existing Tool assignment
path. Disabled/unavailable Tools and managed deny hooks fail before effects. A Tool
requiring approval cannot be called through raw MCP; use the existing approved Agent
invocation. Functional acceptance remains an explicit review with evidence, independent
of technical Flow completion. No task tracker, template or model is needed for the
local deterministic execution path. `dryRun` validates declared structure without
writes and reports that canonical execution validation is still required.

## Optional external task operations

A Project can declare several named Tracking requirements and explicitly assign each
to a compatible local ConnectedService, as described in [Project contexts](projects.md).
`bindingName` selects that logical requirement; exact service/type and derived
Describe Tool/provider/registration evidence are retained. ATC targets need no repository.
Local plans and internal child tasks continue to work without a tracking connection.
External writes retain their own private receipts and never activate or accept a plan.

| Endpoint under `/api/workspaces/{workspaceName}/projects/{projectId}` | Result |
| --- | --- |
| `GET /task-provider?bindingName=tracker&operation=Publish` | Resolve one authorized capability route |
| `POST /task-operations` | Admit and invoke an operation; return its receipt with HTTP 202 |
| `POST /task-operations/{operationId}/resume` | Explicitly recover an uncertain operation |
| `GET /task-operations/{operationId}` | Read the private receipt |
| `GET /task-operations?skip=0&take=25` | Page private receipts, up to 100 per page |

For a Tracking connection, an input uses `tracking: {providerId, targetId}` and omits
`repository` (its default is the empty string). Both identifiers must equal the
selected connection. `Read` selects `taskId`; `Publish` supplies `title` and optional
`body`. All other operations require `taskId` and the observed `expectedRevision`:

- `Update` changes `title`/`body` and adds `labels` as native classifications. It does
  not replace classifications or implicitly change state.
- `Comment` supplies `comment` text.
- `Relate` supplies `relation: {kind, item: {providerId, targetId, itemId}}` using
  `Parent`, `Child`, `Related`, `Blocks` or `BlockedBy`; unsupported relations fail.
- `Transition` supplies the exact native `transitionId` from an observation.

Each request performs one ATC mutation. Publication with labels, composite relations,
or arbitrary state writes is rejected rather than partially applying several writes.
Snapshots preserve the provider-qualified identity, exact revision, native state and
completion reason, classifications, complete relation observations, comments and
available transitions in `task.tracking`. No observation grants functional acceptance.

A request also includes a stable `id`, `expectedProjectVersion` and `bindingName`.
Optional `planId`, `planRevision` and `planDigest` must be supplied together. REST and
authorized Work planning Tools invoke the same application service; no agent Run or
LLM is needed. Private receipt reads and resumption remain scoped to the Workspace,
principal and Project.

Infrastructure uses the independent typed ATC contract
`agentstration.tracking.contracts.mcp.v1` through the governed Tool pipeline, not a
vendor MCP schema. It validates current grants, Describe capabilities, configured
target and all native schemas; retained route fingerprints fence changes before a
remote call. Work stores only neutral capability/evidence records, the local intent
and a separately pinned ATC wire intent digest before any external effect.

A lost response, cancellation, invalid receipt or incomplete observation remains
`Uncertain`. Explicit recovery retains the original operation key and intent. A
correlated `Missing` can authorize re-execution only with durable recovery, within
the declared receipt window and under the same current binding and plan intent.
Process-local recovery may recover an existing receipt, but a missing or expired
receipt cannot justify another publication. Providers with no recovery leave writes
uncertain for operator reconciliation. Concurrency is exposed as actually declared
(`None`, `Observation`, `AtomicRevision`); Agentstration does not strengthen it.
ATC v1 admits no partial item in a non-applied receipt: invalid partial evidence stays
uncertain and is never presented as a successful linkage.

The existing native `TaskOperations` contract and published relays remain used by
current Coding consumers until their own ATC adoption (#1346/#1349). They retain
repository inputs and `agentstration.work.task-provider` metadata. A `Tracking`
connection never falls back to that native protocol. Concrete GitHub/AFC adoption is
separate; see [Coding GitHub connections](coding-github.md).

Task attempts also expose [revision-pinned execution evidence and paginated output history](task-execution-evidence.md). Retaining evidence never accepts a task automatically.
