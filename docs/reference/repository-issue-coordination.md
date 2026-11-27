# Repository issue coordination

Coding can associate one repository-qualified issue with one shared realization plan. The shared
plan owns internal tasks and allocation; the tracker owns native issue hierarchy and cross-plan
`blocked-by` relationships. See [ADR-0276](../decisions/0276-repository-issue-authority-gates-shared-plan-intake-and-dispatch.md).

## Configure and acknowledge

Configure [Project sharing](project-sharing.md), then explicitly assign a named Tracking
requirement to a compatible local ConnectedService. The requirement must include `read` and
`update` and pin the external `providerId` and `targetId`. Publish the approved requirements in
the canonical `ProjectMetadata/project` record before intake. Other participants explicitly
apply those declarations and assign their own services; local UIDs/scopes/accounts may differ.

The GitHub adapter consumes canonical ATC through the governed Tool pipeline. It validates
actual target-specific Describe evidence, complete hierarchy/dependency capabilities,
additive classifications, concurrency observation and recovery. API origin, repository,
managed-label policy, native subject and protected credentials belong to the service. The
registration describes only extension transport. The native `TaskOperations` binding is not
an issue-coordination route and is never selected as a fallback.

GitHub label interpretation and completion predicates belong to the Infrastructure adapter.
The Coding service consumes neutral plan/participant observations and native dependency
identities. Repository display names are checked against the service policy; portable identity
uses the actual provider-qualified target and item, independently of those display names.
See [ADR-0294](../decisions/0294-issue-coordination-consumes-named-tracking-authority.md).

Create a local task tree and associated WorkPlan through the canonical services. Then call:

```text
POST /api/workspaces/{workspace}/projects/{projectId}/coding/issues/acknowledge
```

The request contains `trackingConnection` (the explicit requirement name), `id`, `expectedProjectVersion`, `planId`, `repository`, `issueId`, `provider`,
`planLabelPrefix`, `participantLabelPrefix`, `expectedSharedVersion` and `allocation`. For example,
prefixes `agentstration:plan:` and `agentstration:instance:` produce labels ending in the portable
plan UUID and stable sharing participant UUID. Prefixes must be non-overlapping. The external
issue number is a canonical positive decimal string. Allocation uses the ordinary shared-plan
Task/Participant identifiers.

Intake publishes the plan's issue association with shared compare-and-swap, then adds the plan
and participant labels through a durable ATC-backed Work operation. It does not activate a plan or
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
Only native ATC `BlockedBy` relationships introduce cross-plan waits. Every upstream target resolves through its own explicitly named shared issue connection; no first-match service or cross-target fallback is permitted. Native parentage is descriptive:
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

## Local routes and explicit reconciliation

Shared issue references retain `connectionName` and `tracking: { providerId, targetId }` with
the plan identity and bounded repository/policy declarations. Strict sharing validation requires
that name and target to match the canonical Project requirements. A renamed repository or
another local service UID cannot create a second realization plan for the same native item.
This extends the unreleased YAML contract; existing unqualified fixtures must be reseeded.

Each observation and effect pins the currently verified private route before reserving Work.
A configuration change between policy resolution and operation admission fails before the
provider call; existing late service/Parameter checks still run immediately before invocation.
Confirmed intake receipts retain the exact local service, schema and contract evidence. A new
local service assignment cannot reuse an old acknowledgment, even for the same external issue.
Use a new explicit intake command to reacknowledge through the new authorized service. The
original operation remains retained; uncertain operations are never retargeted or blindly replayed.
Previously admitted attempts keep their original evidence and continue under their existing
execution rules. Readiness returns an opaque local `connectionDigest` for admission correlation,
never a credential or shared route snapshot.
