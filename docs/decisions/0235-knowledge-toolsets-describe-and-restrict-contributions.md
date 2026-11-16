# ADR-0235: Knowledge ToolSets describe and restrict published contributions

## Status

Proposed

## Context

[#1189](https://github.com/gbaudrit/agentstration/issues/1189) requires inspectable
Tool proposals before publication. [#1192](https://github.com/gbaudrit/agentstration/issues/1192)
requires one source-owned ToolSet with integral discovery and separate access to
heterogeneous Data Source contributions. ADR-0234's metadata/content separation
must remain intact. Existing native structured access addresses a whole sealed
publication; filtering returned rows would not isolate aggregates or pagination.

## Decision

Knowledge calculates a bounded proposal from the saved source definition,
resolved bindings, metadata-only readiness/capability descriptions and, where
permitted, current Snapshot metadata. Calculation creates no Tool, publication,
acquisition, content read or lease. It compares stable routes/contracts with the
last published exposure and distinguishes added, modified, removed, unchanged
and unavailable operations. Unknown content availability is not success.

The Console explicitly refreshes this proposal and sends its digest when
publishing. Publication recalculates the same proposal and refuses a different
digest before writing Tools. Source identity/generation is checked again before
ToolSet publication. Existing direct publication callers without a digest retain
their explicit publication operation; they do not claim a prior preview review.
There is no distributed transaction across ToolDefinition, immutable exposure
and ToolSet writes. A failed final publication may leave unassigned versions,
never an automatically adopted Agent/Flow assignment.

Each configured binding produces source-owned read/regex and eligible structured
query relays with fixed contribution selection. Business descriptions are binding
metadata. The contribution identity combines the binding name and exact Data
Source UID within its owning Knowledge Source. Two bindings of one Data Source
remain distinct. A rename is a remove/add, not historical retargeting. Display
names and the acquisition input type do not grant capabilities.

Knowledge output publication records server-derived contribution membership on
each Snapshot Artifact before replacing its logical Artifact ID with a catalog
identity. Membership comes from exact prepared input Artifact identity. A result
which cannot be attributed to one binding belongs to a derived contribution of
the source; it is never falsely assigned to an original input. Existing Snapshot
metadata is not rewritten. Historical content with insufficient membership
fails explicitly when contribution selection cannot be established. Normalized
JSON representations of document inputs can coexist with other JSON records in
a compatible structured output; binary files acquire no implicit extraction,
semantic search or structured query capability.

Read and regex selection is enforced before reading/scanning. Their protected
continuations also bind the contribution. Structured selection resolves member
document IDs from exact retained Snapshot evidence and sends them as a trusted,
read-only restriction to the Storage gateway. The gateway requires an exact
publication and explicitly advertised `DocumentSelection` capability. ASP policy
intersection can only narrow this bounded ID set, including an empty set. Its
policy and authorization evidence bind continuations. The PostgreSQL provider
applies a parameterized ID predicate before filtering, aggregates, sorting and
pagination; the deterministic provider does the equivalent. The ASP server
refuses subset policies for providers without `IAspDocumentSubsetProvider`.
Unsupported implementations fail explicitly rather than returning global data.
This adds no query language or physical destination argument.

An immutable `KnowledgeToolExposureVersion`, owned at the publication's consumer
scope, records the proposal and exact Tool references. Every newly published
source ToolSet includes its describe Tool. A literal relay binding fixes that
exposure identity. Describe returns bounded recorded purpose, contributions,
contracts, acquisition dates, access references and optional exact Snapshot
metadata. It neither prepares data nor issues content capabilities. Actual reads
retain current permissions and integrity checks. Relationships are not inferred.

Runtime passes its trusted exposed Tool references through the existing governed
pipeline and relay context. Agent-facing describe filters the exposure to that
assignment; missing assignment evidence fails explicitly. Public business
arguments cannot supply this context. Tool discovery does not add Tools to the
Agent's context and actual invocation still uses existing authorization.

Existing source-wide native APIs remain available. New exposure publication
uses contribution-specific relays and explicit ToolSet adoption. Native input/
output contracts gain contribution/discovery support; old incompatible relay
pins fail explicitly and require new publication/adoption. Initialization target
9 reconciles the additional native Tool and its standard grants. No relational
migration or historical JSON rewrite is required; membership, descriptions,
proposal and assignment fields are additive.

## Consequences

- One source can expose several meaningful access paths without a generic public
  destination selector. It retains one immutable Snapshot/provenance boundary.
- Capability metadata, content format and actual selection support determine
  eligibility. A file Storage is not an OData provider because it stores JSON.
- Backend layout and credentials remain outside model-visible arguments/results.
- Dynamic Snapshot evidence may affect eligibility; it never changes an earlier
  immutable exposure or consumer pin. Content availability remains untested.
- Broad source querying, named Knowledge Queries, joins, vector search and a
  relationship engine are separate evolutions.
- #1185 metadata consultation and #1186 Artifact/member cardinality keep their
  ownership; this change does not introduce a second member catalogue.
- #1190 and #1193 remain deferred reusable UX scenarios until explicit maintainer
  validation and implementation merge. Implementation regressions, existing
  browser repairs and desktop/mobile smoke remain mandatory.
