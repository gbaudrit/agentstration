# ADR-0172: Descendant resource access is a local administrative overlay

Status: Accepted — 2026-10-13

## Context

ADR-0119 introduced explicit descendant-use grants for Secrets and Vaults and stored those grants inside each resource definition. Parameters and Schemas later reused that shape. Extension-managed resources now require the same default-deny descendant authorization, but their definition, version, integrity evidence, and provenance must remain immutable when a local administrator changes access.

Visibility and ownership are already hierarchical. They determine which exact ancestor resources may be discovered by storage queries, but they do not authorize a descendant to reference or use one. A shared access decision must also reject siblings, upward access, cross-tenant targets, stale identities, and ambiguous name-based fallback.

## Decision

- Descendant access policies are provider-neutral administrative overlays stored separately from Management resource documents. An overlay is keyed by the target resource UID and repeats its exact owner scope, namespace, kind, and optional generation or version pin.
- A grant targets one exact descendant `ResourceScopeRef`, declares `discover` and/or `use`, and may explicitly propagate to that target's descendants. Same-scope access remains governed by ordinary RBAC and needs no descendant grant.
- Missing policies and missing actions deny descendant access. Policy creation validates the persisted scope hierarchy; self, sibling, ancestor, unknown, malformed, duplicate, and cross-tenant targets fail with stable errors.
- Evaluation first verifies the exact current resource identity. A mismatched owner, namespace, kind, generation, or version fails closed. Names are never used as an authorization fallback.
- Policy rows have their own ETag and update timestamp. Changing them does not update the target resource generation, ETag, manifest, immutable version, integrity hash, or extension provenance.
- The shared application service owns create/list/update/delete and effective evaluation. It records policy administration and allow/deny decisions through the existing security audit boundary. SQLite and PostgreSQL implement the same store contract.
- Secrets and Vaults use the shared evaluator for functional access. Their existing `usePolicy` API projection remains temporarily readable and writable as a compatibility bridge for the current Console, but persisted Secret and Vault resource documents carry an empty legacy policy; the overlay is authoritative.

## Consequences

- Resource-family services can filter inherited lists with `discover` and guard functional resolution with `use` without inventing family-specific grant semantics.
- Extension-owned content remains byte-for-byte independent from local access administration.
- Optional generation or version pins let immutable or revision-sensitive consumers reject stale grants, while UID-only overlays can intentionally survive mutable resource updates.
- Existing pre-release Management databases require migration or reseeding. No released compatibility guarantee is affected.
- The access overlay is not a portable resource manifest and is not included in Pack or extension hashes.
- This decision supersedes ADR-0119 only where that ADR stores grants inside Secret and Vault definitions. Its default-deny, exact-scope, late-bound Secret resolution, and hierarchy rules remain in force.
