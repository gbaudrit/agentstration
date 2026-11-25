# ADR-0267: Repository issue authority gates shared-plan intake and dispatch

## Status

Proposed — implementation for [#1346](https://github.com/gbaudrit/agentstration/issues/1346).

## Context

Portable shared plans govern internal decomposition and allocation. Repository issues govern
issue identity, native hierarchy and cross-plan prerequisites. Additive GitHub labels are not
an atomic distributed claim, and native closure may mean completion or abandonment.

## Decision

Coding coordinates existing Project, WorkPlan, sharing and task-provider services. It adds no
scheduler. An issue-backed shared plan carries a provider-neutral repository-qualified issue
reference and configured plan/participant label prefixes. An existing association cannot be
removed or replaced by an ordinary sharing publication. Shared CAS rejects two plans for the
same qualified issue, including case variants of the repository name. Ordinary local and
trackerless shared plans retain their existing behavior.

Intake explicitly selects an owned associated local plan. It publishes its issue association
through sharing CAS before writing native labels. Another instance explicitly imports that
same shared plan before acknowledging it; intake never invents an association from a local
cache or an unsupported label alone. Typed documents and local Work execution remain separate.

The native issue connection must be one exact protected published TaskOperations Tool. Coding
negotiates immutable issue-authority metadata against both the publication and its source:
provider identity, complete native hierarchy/dependencies, completion reasons, managed labels
and revision observations. API origin, repository allowlist and label policy are protected
literals; the credential is a Secret. The normal canonical task-provider pipeline reauthorizes
every observation and effect.

Plan labels identify the portable plan; participant labels identify stable sharing participants.
Updates add only managed labels, preserving unrelated labels and native hierarchy. A native
observation is a precondition check, not an atomic GitHub label compare-and-swap. Conflicting,
invalid or multiple plan labels are conflicts before and after publication. Concurrent external
manual edits are rechecked; they are never normalized silently.

Canonical Work task operations retain native write intents and receipts. The logical intake ID
derives stable sharing and issue operation IDs. Replay recovers the same saved intent before
reissuing it. A lost response remains pending until the write receipt is recovered and fresh
native/shared observations agree. Work's private plan mapping retains the confirmed operation
reference, scoped by Workspace, Principal, Project and Plan. SQLite upgrades and PostgreSQL
migrations add that nullable reference. Storage accepts a reference only to a succeeded owned
operation for the same Project and Plan. Restart can finish confirmation without another native
write; native labels alone do not unlock a still-pending local intake.

Readiness refreshes the current native issue and each native `blocked-by` issue. Parent/child
relationships do not fabricate an execution dependency; a native child has its own plan.
Each upstream issue must resolve uniquely to a different shared plan and its current native
plan label must agree. Only `state == closed` with `completionReason == completed` satisfies
an upstream prerequisite. Open, abandoned, unknown, missing, inaccessible or conflicting authority
blocks admission. The current issue must remain open and retain this participant's acknowledgment.

The existing WorkPlan admission gate evaluates this observation before every new local attempt.
It records an opaque digest of root/upstream revisions with the issue/plan identity; the API
exposes the upstream issues/plans and reasons for waits. Sixteen prerequisites and a 45-second
overall observation deadline bound each check. No cached successful read receipt is reused for
fresh eligibility. Native changes refresh eligibility on the next observation. Already admitted
attempts retain local authority and captured evidence; dependency edits do not cancel, retry or
duplicate them. Runtime success never automatically closes an issue or accepts local work.

## Consequences

The first native implementation is the existing optional GitHub extension. Its wire contract
adds an optional completion reason; old null fields and trackerless behavior remain compatible.
No GitHub fields or issue semantics enter the generic Git extension. A future tracker adapter
must advertise and demonstrate the same authority guarantees.

Manual label conflicts, stale preflight updates and divergent shared proposals need explicit
reconciliation and a new intent where appropriate. There is no unsafe last-writer-wins repair,
automatic issue closure or background intake retry. Integrated UI remains with the parent
journeys; this slice supplies typed APIs and deterministic provider/application tests.
