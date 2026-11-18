# ADR-0208: Produced content remains an Artifact materialized in Storage

## Status

Proposed

## Context

The native dataset path in #1041 writes an exact Storage object receipt. The
retained Knowledge path in #1042 prepares and seals Storage publications. Neither
path initially registered every produced result in the Artifact catalogue.
Consequently, valid stored content could be absent from the Artifact screens.

The maintainer clarified the invariant on 2026-11-10: an Artifact represents a
produced and stored element, with visible links to its Storage and producer.
Storage selection must not remove Artifact identity. This extends ADR-0151,
ADR-0153, ADR-0204 and the proposed ADR-0207 without rewriting their history.

## Decision

Reuse the existing immutable `FlowRunArtifact` resource and catalogue. An Artifact
may originate from a sealed staged copy or directly from a producer's confirmed
Storage write. A direct materialization has no fabricated staged source or Storage
FlowRun. It records the actual producer resource UID/generation, producer execution
and FlowRun, media type, length and SHA-256, plus the exact Storage revision,
logical key and available provider version. Retained publications additionally
record their sealed publication evidence.

The Artifact family owns registration and checks physical content through its
durable-storage boundary before creating metadata. Registration identities are
stable within the execution Workspace; conflicting producer or content evidence
is refused. Registration never copies the bytes into another backend merely to
make them appear in the catalogue.

Native acquisitions populate their ordinary Artifact manifest after successful
Storage writing and registration. Knowledge registers the complete sealed output
as Artifacts before publishing its Snapshot. Inline bytes remain a bounded Flow
transport format; they do not replace the identity of a produced Artifact.

The existing Artifact content materialization Flow, local-copy preview and download
remain authoritative. Their Storage adapter reauthorizes exact references, verifies
content integrity and refuses changed or foreign content. Administrative reads
restore only the logical scope belonging to the recorded operational Workspace,
then recheck its current authorization. A regular Workspace cannot promote itself
into Instance or Tenant administration through a stored reference.

The catalogue and Artifact details expose producer and Storage links. Dataset
history and Knowledge publications link to their Artifacts. Existing selectable
local Artifact writes also expose the Storage location on new receipts; historical
independent filesystem receipts keep their existing path.

The existing recovery worker performs bounded metadata reconciliation for native
acquisitions and retained publications created before this correction. It runs as
the recorded producer caller with current authorization. It creates missing
Artifact metadata, attaches missing acquisition manifests and never re-executes an
origin, rewrites stored bytes or modifies an immutable Snapshot. Unavailable or
revoked content remains unavailable; no reference is silently rebound. Metadata
repair is best effort: a refused historical read cannot prevent listing its
history or explicitly starting a new acquisition. Unversioned Data Source reads
verify the complete digest through bounded chunks before exposing a range; a
short read does not by itself mean the historical object has disappeared.

## Consequences

- An Artifact's immutable identity and metadata do not imply that every Storage
  offers immutable retention. Native acquisition content retains the actual
  provider guarantees and fails closed if its exact content is unavailable.
- Knowledge still requires an adapter with retained publication semantics.
  Azure/OpenDAL output qualification remains separate work; no capability is
  inferred from the existence of an Artifact record.
- The initial native and retained publication profiles remain bounded to 64 KiB.
- Storage writes/sealing, Artifact registration and Snapshot activation remain
  separate durable operations. Retries/recovery retain identities; no distributed
  transaction is claimed.
- Existing resources and persisted histories are preserved. No database reset,
  destructive migration or second Artifact catalogue is introduced.
- #1033 retains its explicit implemented-UX approval and merge gates.
