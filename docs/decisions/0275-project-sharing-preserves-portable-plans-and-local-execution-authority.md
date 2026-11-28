# ADR-0275: Project sharing preserves portable plans and local execution authority

- Status: Proposed — implementation for #1345
- Date: 2026-11-25
- Owners: Work Plane
- Related: [#1345](https://github.com/gbaudrit/agentstration/issues/1345), ADR-0302, ADR-0303, ADR-0274

## Context

Independent developer instances need the same decomposition and allocation without exchanging
Work databases, local resource identities, credentials or execution ownership. A shared Git
branch supplies concurrency and history; it must not become another scheduler or Run store.

## Decision

A private Workspace/Principal Project may declare one sharing connection: a portable Project
identity, a stable participant identity, a logical location and an exact authorized binding.
`IWorkProjectSharingGateway` negotiates versioned read, write, exact revision, compare-and-swap,
history, idempotency and recovery guarantees. Ordinary local Projects have no remote requirement.

Work owns a portable version 1 format with five document domains: Bootstrap profiles, Project
metadata, plans, contributions and usage. Plans contain immutable task declarations, dependency
intent and atomic-task allocation. Local snapshots, attempts, credentials, Workspace/Principal
IDs and provider coordinates are not portable identities. Typed Bootstrap declarations prohibit
physical scope/UID and credential fields; named local references remain declarations.

The Git adapter in Infrastructure calls the existing protected published Tool through the
normal authorization and execution pipeline. Its source and published capabilities must both
meet the contract. Repository, branch, path policy and authentication are protected literals;
the credential is a Secret binding. Infrastructure mirrors the native wire DTOs and does not
reference the autonomous extension assembly. Git interprets the logical location as a bounded
relative path; the Work contract has no Git fields.

The provider-owned representation is an index plus immutable, versioned, digest-addressed
record files. The initial executable limits are 64 current records, 16 KiB per content document,
1 MiB per snapshot and 16 KiB of changes per command, with a 40 KiB index/record publication envelope. Native writes must additionally fit the
existing protected relay input bound; this bound is not increased. Exact historical reads use
retained revisions. There is no deletion or garbage collection operation in version 1.

Work SQLite and PostgreSQL persist authorized projections, plan mappings and publication intents.
Admission atomically checks the observed local Project and plan versions before any remote effect.
Each logical operation retains an append-only sequence of native attempts, each pinned to its
base, complete index, records and opaque operation identity. Recovery precedes resubmission.
An uncertain native result cannot be rebased. A proven native CAS conflict permits a fresh
attempt only after rereading the space: disjoint records merge, while a changed expected document
version surfaces a conflict. Contributions and usage identities are immutable; identical content
is deduplicated. A logical operation has at most eight native attempts.

Pull stores projections and never changes execution or Bootstrap configuration. Explicit plan
import reuses the canonical task-tree and plan services, creates an ordinary local draft and
associates its private Project. Replay resumes the same immutable declarations and command;
a different local proposal or changed immutable task tree causes an explicit conflict. It never
activates a plan, accepts an outcome or replaces a running proposal.

The existing WorkPlan admission gate refreshes shared plan and allocation evidence before each
new local attempt. It verifies the local declarations and dependencies and requires the task's
allocation to this participant. A mapped plan cannot bypass the gate by omitting the Project.
Evidence pins the observed revision, digest and participant. This is an authorized observation,
not a distributed execution lease; concurrent external assignment changes do not cancel an already
admitted attempt. Existing attempts retain their evidence and local execution authority.

The Workspace `ProjectSharing` Bootstrap handler resolves a pre-provisioned protected Tool by
local semantic name and pins its current identity. It creates the private Project idempotently,
skips an equivalent connection and reports conflicting configuration. Bootstrap application,
initial pull/import and publication recovery remain explicit separate operations. Synchronization
never automatically applies a remote Bootstrap profile or upgrades local configuration.

## Consequences

- Local execution remains offline by default and private to its owner.
- Shared plans govern decomposition and allocation; local Work governs attempts and outcomes.
- A lost response is observable and resumable; conflicts never use last-writer-wins replacement.
- Participant identity must be stable and distinct for independent participants; cloned local
  configuration must receive a new participant identity before it joins as another participant.
- Git branch protection and retained history remain operator responsibilities. Unsupported
  capability, stale binding, denied access or unavailable history fails explicitly.
- Usage transport is executable here; automatic usage attribution belongs to #1348. GitHub issue
  authority belongs to #1346. Workspace seed application belongs to #1347.
