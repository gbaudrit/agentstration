# ADR-0234: Knowledge consultation reads only metadata

## Status

Accepted

## Context

[#1185](https://github.com/gbaudrit/agentstration/issues/1185) separates Knowledge
Source consultation from content access. Snapshot inspection currently probes
every retained Artifact. For structured publications, a one-byte probe retrieves
the entire document and repeats protected provider sessions and Secret redemption.
History browsing therefore depends on content permissions and provider health,
and its physical I/O grows with the number of Snapshots and Artifacts.

ADR-0153 established immutable publication evidence and inspection-time detection
of missing content. ADR-0207, ADR-0208, ADR-0210 and ADR-0224 subsequently introduced
retained Storage publications and native, exact-Snapshot content access.

## Decision

Snapshot list, detail, active-pointer inspection and `get_status` use authorized
persisted metadata only. They do not resolve Artifact availability, inspect
physical publications, read bytes, create content leases, start preparation or
open protected provider sessions. The same rule applies to structured capability
descriptions, including an explicitly selected Snapshot. Scope and Source identity
checks remain mandatory.

Lifecycle and physical content availability are separate response fields.
`Active`, `Superseded`, `Retiring` and `Retired` come from stored lifecycle evidence.
Metadata responses report `ContentAvailability` as `Unknown` for unprobed content;
metadata-known retirement is `Retired`. Successful reads do not update a
provider-health cache. `ready` in
informational status describes publication/freshness evidence, not current
physical availability or content authorization. `CheckedAt` dates that metadata
evaluation, not a provider probe.

Actual reads retain current content authorization, exact immutable references,
version/binding validation, bounded reads and digest checks. They validate the
selected Artifact, without probing unrelated Artifacts. Structured queries
validate their exact pinned publication and current rights before executing the
bounded query; they do not retrieve every document first. Cursors remain bound
to their original Snapshot even after another Snapshot becomes active. Retired
content remains inaccessible, and retiring Snapshots reject new leases under the
existing retention policy. Explicit activation remains a separate write operation
with its existing content validation.

This decision replaces only ADR-0153's inspection-time availability policy.
Immutable publication validation, provenance, access isolation, retention and
exact-reference guarantees remain unchanged. No migration or credential cache
is introduced.

## Consequences

Metadata stays inspectable during a provider outage or without content-read
permission. A Snapshot may remain active while an actual content operation fails;
the failure does not erase metadata or silently select another publication.
The Console labels content availability as not checked and explains when it is
verified. Call-count regressions enforce the absence of physical I/O regardless
of history size. The deferred reusable browser scenario is #1187; it remains
blocked on explicit maintainer UX validation and implementation merge.
