# ADR-0242: Reconciliation preview reviews selected authorized evidence

## Status

Proposed

## Context

[#1199](https://github.com/gbaudrit/agentstration/issues/1199) needs an explicit
review before family-owned publication or adoption. Its first Agent execution
cohort depends on the undecided publication contract in #1031. Existing Resource
ChangeSet operations do not model publication/adoption (ADR-0120/0144–0146).
A read-only review can proceed independently of those execution prerequisites.

## Decision

`Reconciliation` owns a separate, read-only preview application service. It consumes
neutral authorized SystemGraph inventory/impact ports, never LikeC4 or family
mutation/storage services. It accepts an exact previous/target identity pair,
1–32 explicitly selected consumer identities and optional proposed publication
versions. The Console uses `/system-explorer/reconciliation`; the same service is
exposed through POST `/api/system-reconciliation/preview` and `/revalidate`.
Both routes require current resources/read and return no-store responses.

The review retains selected dependency paths, exact identity/scope/namespace,
recorded generations/publication hashes and available current authoring ETags.
Native Agent revisions resolve their current authoring definition through the
authorized publication relationship, never a revision-name join. Current consumer
edits therefore invalidate the review even when historical revisions stay unchanged.
Steps follow selected dependency precedence with deterministic topological ordering.
Logical and operational edges are already excluded by the impact service. Compatible
dynamic Query bindings produce no-op steps; a contract mismatch remains blocked
without choosing an older publication. Agent adoption is blocked pending its
approved owning-family primitive. Publication is blocked pending a reviewed
conditional command; unsupported family actions remain unknown.

No draft, publication, grant, default, execution, provider session, content lease
or durable operation receipt is created by preview or revalidation. The result
always has CanExecute=false and GloballyComplete=false. This increment exposes
no executor endpoint, acceptance command or readiness/activation control. A
blocked review must not be confused with an executable plan.

Construction uses repeated authorized observations. Neighbor-page digests describe
slices and cannot be equated to the impact service's whole observation digest.
After bounded authoring reads, the service repeats the full impact comparison
and authorization context check; a changed observation requires another review.
This remains optimistic observation, not a cross-store transaction.

The review fingerprint binds the authenticated context, selected identities,
explicit target choices, relevant returned nodes/relations and step/blocker facts.
It excludes sample time and the whole-graph digest. Revalidation rebuilds current
authorized evidence: a relevant edit, identity replacement, changed selection or
revocation invalidates the old fingerprint; an unrelated resource change need
not invalidate it. Missing/invisible sources return no consumer evidence, names
or private totals. Generic errors never return raw exceptions.

The Console first compares exact evidence, then requires explicit consumer/version
selection before building a review. Current context and namespace come from the
URL. Source/selection/version changes invalidate pending responses. EN/FR loading,
unknown, partial, blocked, stale and no-op states preserve the distinction between
adoption, publication and runtime readiness. No renderer participates.

## Consequences

A useful deterministic review can ship while execution prerequisites remain open.
This is a partial delivery of #1199, not its completion. Before adding execution,
the owning feature must provide approved Agent adoption semantics, conditional
family operations for the complete relevant read set, current rights/managed
resource authority, durable bounded operation identity and per-action receipts,
idempotent uncertain-completion recovery, cancellation and truthful partial
outcomes. ADR-0120's ordering/lease patterns may be reused where applicable;
Create/Update/Delete/NoOp must not be relabelled as publication.

[#1202](https://github.com/gbaudrit/agentstration/issues/1202) remains deferred until
explicit validation of the implemented UX and merge of its UX PR. Existing feature
and offline regressions remain required now. No parent issue is auto-closed.
