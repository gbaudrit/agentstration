# ADR-0235: One Artifact owns a collection of JSON records

## Status

Accepted

## Context

[#1186](https://github.com/gbaudrit/agentstration/issues/1186) separates logical
Artifact identity from the granularity needed for document queries. The existing
`artifact.explode` function creates independent Artifacts. Using it solely to
query individual values multiplies catalogue resources unnecessarily.

The acquisition envelope and its root Schema remain useful evidence. A selected
record needs its own exact contract, including the parent publication and shared
Schema sub-schema pins. An item contract cannot describe a whole JSON array.

## Decision

This refines ADR-0233's final JSON storage handoff: arbitrary JSON remains valid
inside a Flow, while persisted JSON uses the explicit object-record collection.

JSON publication accepts a non-empty, ordered collection of objects. Without an
explicit record transformation, an acquired root object becomes a singleton
collection and its root contract becomes the record contract. Plain arrays and
scalar results are rejected at this boundary. `json.explode@1.0.0` explicitly
selects an array of objects by JSON Pointer and validates each object against a
required `itemSchema`. It composes at Data Source transformations, Knowledge
binding transformations and post-aggregate transformations. It retains one output
Artifact per input Artifact. `artifact.explode` retains its separate meaning:
creating independent Artifacts.

The logical content is the canonical JSON array of records. Its whole-content
contract is derived as an array whose `items` is the record schema. Published
Schema UID, version, generation, parent hash, selected path and selected hash
remain on the record contract; the derived array does not claim that evidence.
Raw acquisition and accepted Flow/schema evidence remain separate.

A document publication materializes one immutable document per record, so each
row can be queried independently through the existing governed query boundary.
Knowledge Snapshots can share one atomic document publication across several
Artifacts while recording each Artifact's exact ordered membership. The existing
publication digest and cursor scope continue to identify the selected acquisition
or Snapshot after a newer publication becomes active.

Built-in local Storage materializes one physical JSON file containing an object
per record, accompanied by integrity receipt sidecars. It creates no aggregate
JSON file. The logical array is reconstructed and verified only on explicit
whole-content reads. Local Storage gains no document-query capability.

An immutable Artifact receipt records member identifiers, order, lengths and
digests. No member read accepts a caller-supplied Storage target or path. Member
reads start from an authorized Artifact, enforce current content permissions and
retention admission, and verify the selected member's exact publication evidence.
Metadata inspection enumerates persisted membership without reading content,
consistent with ADR-0234. Structured query responses include document-to-Artifact
and record-ordinal lineage without fetching every member first.

Collections contain 1–100 objects and at most 64 KiB of canonical aggregate JSON.
Empty selections, non-object members, incompatible contracts and over-limit
collections fail explicitly; no truncation is permitted. Existing Storage and
collection limits may narrow these limits further. Preparation and sealing precede
Artifact registration and activation. Accepted Data Source deadlines and prepared
membership are persisted before sealing, allowing retries with the same identity.
Partial output cannot become an active Snapshot. Local unsealed leftovers remain
in the existing bounded pending-publication cleanup lifecycle. Retirement removes
the entire member set through its publication boundary and honors active leases.

These formats have not shipped. At the maintainer's explicit request, there is no
compatibility reader, migration or historical-receipt conversion. Stored Artifact
references use `artifact-storage-v3`. Generic binary/text Artifact storage remains
available independently of the JSON record projection contract.

## Consequences

Catalogue size depends on logical Artifacts rather than row count. Document queries
can select individual records, and local users receive actual separate files.
Whole-content reads reconstruct the bounded array and cost one read per member;
member reads avoid reading unrelated members. Consultation remains metadata-only.

This increment deliberately retains bounded publications. Streaming, unbounded
datasets, arbitrary SQL and new query capabilities for file Storage are outside
its scope. Real PostgreSQL validation remains an explicit integration lane;
deterministic loopback document tests do not constitute PostgreSQL evidence.

The deferred reusable UX scenario is tracked by
[#1188](https://github.com/gbaudrit/agentstration/issues/1188) and starts only after
maintainer UX validation and merge of the implementation pull request.
