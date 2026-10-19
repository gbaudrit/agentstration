# ADR-0103: Source registry trust evaluates independent evidence dimensions

- Status: Accepted
- Date: 2026-09-10

## Context

A registry origin, its assertion about a publisher, the bytes of one SourceVersion manifest, and materialized mutable Channel content answer different security questions. Treating them as one trusted flag would allow a familiar hostname or verified definition to imply claims that were never evidenced. Registry observations are immutable provenance, while local policy, revocation, and conflicts can change the current decision.

## Decision

Agentstration evaluates four independent dimensions:

- origin classification identifies the stable built-in official registration, informational Agentstration-owned HTTPS origins, internal literal origins, and external origins;
- publisher assertions are accepted only to the level allowed by the registration's current `untrusted`, `trusted`, or `authoritative` policy;
- exact SourceVersion verification continues through the existing verification evidence-provider boundary and requires publisher, Source name, opaque version, and canonical manifest digest;
- Snapshot verification remains an independent exact Channel, revision, and archive-digest decision.

The official classification requires the persisted well-known registration name, non-empty stable UID, and built-in provenance annotation. Hostname matching alone never grants trust. Agentstration-owned host matching accepts only default-port HTTPS on the bare domain or a dot-delimited subdomain.

Every contributing current observation is returned as evidence. Accepted revocation dominates positive publisher assertions. Different accepted manifest digests for the same identity and version produce a blocking conflict. Equal evidence is retained rather than collapsed. Current evaluation is recomputed from registration policy and observations; cached observations and historical refresh records are not rewritten.

## Consequences

Policy downgrade, disablement, removal, or new revocation changes current evaluation immediately without mutating historical provenance. Later discovery and import work can persist the returned evaluation snapshot with an operation. API responses expose identifiers, digests, statuses, reason codes, origin, and evaluation time without exposing credentials or cached registry documents.

Registry evidence can verify exact SourceVersion bytes, but it cannot verify a mutable Channel or Snapshot. Mandatory signatures, transparency infrastructure, discovery merging, exact import, and Console presentation remain separate increments.
