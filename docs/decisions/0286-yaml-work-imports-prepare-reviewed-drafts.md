# ADR-0286: YAML Work imports prepare reviewed drafts

- Status: Accepted — implementation for #1363
- Date: 2026-11-25
- Related: ADR-0300, ADR-0275, ADR-0284, ADR-0285, #1364

## Context

The minimal demo prepares missions through imperative operator calls. Versioned tracking YAML
needs an executable, reviewed path into the existing Work services without converting configuration
ingestion into a scheduler or requiring optional templates/providers.

## Decision

One explicit bounded `data/catalog.yaml` selects WorkPlan manifests, which reference task-tree
manifests. Stable portable IDs are declared in content; paths are organizational. Configuration
and Flow definitions stay under Bootstrap. The same strict codec resolves uploaded local content
or exact-revision content acquired through the protected Project Git connection.

Application owns validation/review/preparation; Work owns portable definitions, identities and
operation state. Both REST input forms delegate to one service. Structural normalization reuses
the WorkPlan/task-tree rules and rejects ambiguous paths/identities, graph cycles and incompatible
local work. Source digests pin reviewed bytes and relevant local observations. Issue declarations
do not acknowledge or mutate a source issue; canonical issue intake/readiness remains required.

Before mutation the Work store retains a frozen owned operation under Workspace/Principal/Project
and guards the reviewed Project/plan/task observations. Per-plan progress and deterministic canonical
creation/association command IDs make interrupted and lost-response replay safe. SQLite and PostgreSQL
store private operation documents in the Work Plane with optimistic concurrency. No Management or
Runtime store is reused. Partial outcomes are explicit, not a cross-module transaction promise.

The importer creates/reuses only ordinary draft declarations. It never revises divergent authored
work, applies configuration, activates/allocates/executes a plan, accepts outcomes or writes to a
repository provider. File moves require a new source review and retain functional identities.
Execution later resolves installed published Flow/native inputs through the normal Work path.

## Consequences

Generic local work remains executable without a tracker or connector. Imports can be inspected and
explicitly resumed after restart without a second scheduler. Existing bounded histories/authorities
still apply. The API slice introduces no Console/Workplace journey; #1347/#1350 and #929/#930 retain
their integrated UX ownership and deferred validation/merge gates.
