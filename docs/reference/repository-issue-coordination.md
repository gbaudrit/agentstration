# Repository issue coordination

Coding can associate one repository-qualified issue with one shared realization plan. The shared
plan owns internal tasks and allocation; the tracker owns native issue hierarchy and cross-plan
`blocked-by` relationships. See [ADR-0276](../decisions/0276-repository-issue-authority-gates-shared-plan-intake-and-dispatch.md).

## Configure and acknowledge

Configure [Project sharing](project-sharing.md) and exactly one protected published
`TaskOperations` binding. The GitHub issue Tool advertises `agentstration.coding.issue-authority`
version `1.0`, with provider `github` and complete native hierarchy, dependency, completion-reason,
managed-label and revision-observation guarantees. Copy that capability declaration as well as
the ordinary task-provider declaration when publishing the protected relay. Both immutable source
and publication are checked. Bind API origin, repository/native-ID allowlist and managed label
prefix as literals, and credential as a Secret. The label prefixes used below must fall within
that protected managed prefix.

Create a local task tree and associated WorkPlan through the canonical services. Then call:

```text
POST /api/workspaces/{workspace}/projects/{projectId}/coding/issues/acknowledge
```

The request contains `id`, `expectedProjectVersion`, `planId`, `repository`, `issueId`, `provider`,
`planLabelPrefix`, `participantLabelPrefix`, `expectedSharedVersion` and `allocation`. For example,
prefixes `agentstration:plan:` and `agentstration:instance:` produce labels ending in the portable
plan UUID and stable sharing participant UUID. Prefixes must be non-overlapping. The external
issue number is a canonical positive decimal string. Allocation uses the ordinary shared-plan
Task/Participant identifiers.

Intake publishes the plan's issue association with shared compare-and-swap, then adds the plan
and participant labels through a durable task-provider operation. It does not activate a plan or
create tracker issues for internal tasks. The response is `202` with Pending or Confirmed state,
related operation IDs and the observed native revision. Conflicts return `409` with intake detail.
Replay the same logical ID and original intent to recover a pending publication or acknowledgment;
use a new ID for a changed intent. An uncertain write is not confirmed solely from a local cache.

A second instance first pulls and explicitly imports the existing shared plan, then acknowledges
that same plan. It uses its own participant label and preserves the authoritative shared allocation.
A different plan label or shared association reports a conflict. Normal sharing cannot remove or
replace an existing issue association. Changing native labels manually is detected by fresh reads.
Unrelated labels and existing native parents are preserved.

## Observe waits and dispatch

```text
GET /api/workspaces/{workspace}/projects/{projectId}/coding/plans/{planId}/issue-readiness
```

The result reports issue eligibility, native revision, observation time, waits and observed
prerequisites. Each wait names the upstream issue and its different shared plan when known.
Only native `blocked-by` relationships introduce cross-plan waits. Native parentage is descriptive:
a child has its own plan but does not automatically wait for its parent.

An upstream issue satisfies a prerequisite only when it has one matching shared plan/current
native plan label and is closed with completion reason `completed`. Closure for `not_planned`,
unknown completion, missing plan, unavailable repository, incomplete observations and conflicting
labels remain blocked. The current issue must be open, retain this participant's label and have
a confirmed durable local acknowledgment receipt. A missing receipt after a lost response remains
pending until intake recovery completes.

This response describes repository authority; canonical WorkPlan activation, functional task
acceptance, internal edges and shared allocation still apply. The existing WorkPlan admission
path rechecks repository authority before a new attempt and pins root/upstream observation evidence.
Removing or changing native dependencies refreshes the next check. Already admitted attempts
retain their captured authority; a dependency edit does not cancel them or create another attempt.
No runtime completion automatically closes an issue.

Each observation is bounded to 16 native prerequisites and 45 seconds overall. Calls require the
current private Workspace/Principal, Runs Execute and current binding Discover/Use authorization.
Trackerless local Projects remain offline. Optional live GitHub tests are separate from the
required deterministic fixtures; no live credentials or LLM are required.
