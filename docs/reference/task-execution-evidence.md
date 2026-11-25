# Task execution evidence and history

An atomic functional task keeps every explicit WorkPlan attempt. Technical success does not accept
the task: use the existing task outcome command after reviewing the retained evidence. See
[ADR-0269](../decisions/0269-task-execution-evidence-projects-canonical-plan-attempts.md).

All routes are under `/api/workspaces/{workspace}` and require the authenticated private task owner,
current Workspace and the normal Run read/execute permission. Another Principal or Workspace
cannot use IDs to retrieve its history or attach evidence.

| Route | Behavior |
| --- | --- |
| `GET /tasks/{task}/execution-history?skip=0&take=25` | Canonical attempt receipts, including exact plan/task pins, execution identities, terminal snapshots and retained evidence. |
| `GET /tasks/{task}/results/page?skip=0&take=25` | Task-specific persisted result contents and their Flow Run correlation. |
| `GET /tasks/{task}/artifacts/page?skip=0&take=25` | Task-specific artifact descriptors/content links without storage keys. |
| `POST /work-plans/{plan}/tasks/{task}/attempts/{attempt}/evidence` | Attach immutable, revision-pinned evidence to the current terminal attempt. |

Pages expose `items`, `skip`, `take`, `totalCount`, `hasMore`. Skip is non-negative and take is 1–100.
Attempt order is admission time then ID. Result/artifact order is stable ID; timestamps and sequence
remain in each item. Counts describe this exact task, independently of global Run volume. The
existing [task-specific Flow history](work-plans.md) remains available separately. Pagination reads
observe current committed state; concurrent append operations can change subsequent page counts.

Evidence bodies contain:

```json
{
  "eventId": "<new-guid>",
  "expectedPlanVersion": 7,
  "expectedTaskVersion": 3,
  "admittedTaskVersion": 1,
  "taskDigest": "<exact-admission-digest>",
  "summary": "Reviewed execution output retained for later functional review.",
  "resultIds": [],
  "artifactIds": []
}
```

Use the current plan/task versions and the exact attempt's admission pins. An unfinished attempt,
an old attempt after a new retry, a stale version or changed pin returns `task_execution_evidence_stale`
(409). References must exist for this exact task/Workspace/Flow Run, otherwise the command fails
with `task_execution_evidence_reference_invalid`. Child-Flow outputs require a matching authorized
task/scope/root-Flow observation. Up to 64 distinct result IDs and 64 distinct
artifact IDs, an 8,000-character summary and a 32 KiB HTTP request are supported. No content is
copied from a foreign artifact. The command does not create a result/artifact or mark it accepted.

Identical replay retains the original evidence/plan version. Another payload under the same event,
or replacement of already attached evidence, conflicts. A storage failure rolls back both the plan
and its history projection; retry the original event and body after the failure is resolved.

Receipts expose the stable attempt ID, plan/revision/digest, task/admission version/digest, execution
WorkItem ID and root Flow Run ID. These are the correlation coordinates for later local invocation,
usage and delivery evidence; functional task identity survives retries. No provider inference, token
count or functional acceptance is inferred from a receipt. Historical attempts created before this
change retain their coordinates and existing WorkItem/Flow/output history; a missing newly added
terminal snapshot stays absent.
