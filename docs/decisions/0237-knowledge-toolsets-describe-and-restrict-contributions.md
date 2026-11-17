# ADR-0237: Knowledge ToolSets describe and restrict published contributions

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
query relays with fixed contribution selection. Business descriptions are live editorial metadata. An input description overrides
the bound Data Source description; an empty override inherits it. Resolving this
metadata follows the exact bound Data Source UID and never retargets a recreated
resource with the same name. The contribution identity combines the binding name and exact Data
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
exposure identity. Describe without `input` returns a lightweight catalogue: effective business
purpose, available input identities, formats and assigned Tool references. It
omits full Tool contracts, schemas and document-ID lists. Each exposed input has
its own describe relay with fixed input selection and a `{ "request": {} }` call.
Discovery returns explicit input names/identifiers, assigned describe callable
names and complete argument examples. The source-wide describe also accepts an
exact exposed route identifier or unambiguous binding name for older exposures.
Input-level discovery returns only that exposed input's detailed contracts, schemas, provenance and
optional exact Snapshot metadata. Unknown or unassigned inputs fail explicitly.
Current editorial metadata overlays the immutable execution exposure; exact
selections, schemas, profiles and publication references stay pinned. It neither prepares data nor issues content capabilities. Actual reads
retain current permissions and integrity checks. Relationships are not inferred.

Runtime passes its trusted exposed Tool references through the existing governed
pipeline and relay context. Agent-facing describe filters the exposure to that
assignment; missing assignment evidence fails explicitly. Public business
arguments cannot supply this context. Tool discovery does not add Tools to the
Agent's context and actual invocation still uses existing authorization.

Business description/display-name edits preserve the executable generations of
Data Sources and Knowledge Sources and their accepted dependency pins. Their
ETags and audit history still change. Input description overrides are editorial
as well. Contract and proposal digests exclude this editorial text, while
execution-affecting configuration, eligibility and capability changes remain
reviewed publication inputs. Editorial updates neither rebind active dependencies
nor require a new ToolSet version, Agent revision or Snapshot.

A provider-neutral runtime presentation enricher prepares model-visible Tool
descriptions and a system-prompt catalogue from the actual resolved assignment.
The Knowledge implementation reads only the assigned immutable Tool publications
and authorized source metadata. It lists only sources with an assigned describe
Tool and instructs the Agent to discover inputs before first data access, obtain
selected-input contracts when needed, and reuse evidence for the same exposure
and Snapshot. It directs the Agent to consult every available input before
synthesis and distinguish data consultation from metadata discovery. Instructions
and schema guidance derived from the assigned immutable contract survive a failed
live Source metadata lookup; only that Source's current editorial overlay is
omitted. No new live metadata or content permissions are granted. Diagnostic logs
and trace counters report presentation coverage without storing prompts/content.
Direct MAF creation and AWP materialization use this same boundary.
This runtime presentation does not rewrite authored instructions, revision hashes,
Tool references, published schemas, approval flags or grants. For an exact pinned
OData profile, model presentation narrows the generic `request.query` value to
its supported object/string-option schema and requires the `request` envelope.
The same presentation applies to detailed discovery and AWP model material;
publication and authoritative execution retain their immutable contracts.

Existing source-wide native APIs remain available. New exposure publication
uses contribution-specific relays and explicit ToolSet adoption. Native input/
output contracts gain contribution/discovery support; old incompatible relay
pins fail explicitly and require new publication/adoption. Initialization target
10 reconciles the additional native Tool and its standard grants. No relational
migration or historical JSON rewrite is required; membership, descriptions,
proposal and assignment fields are additive.

The JSON record model from ADR-0235 remains independent of contribution identity.
A selected logical Artifact contributes all its immutable member document IDs to
the provider selection before filtering, aggregation and paging. Query results
retain the logical Artifact and member ordinal, and continuations preserve the
same contribution. Detailed discovery distinguishes whole-Artifact schemas from
record schemas. Operator content ceilings from ADR-0236 continue to narrow the
applicable Storage and acquisition budgets; Tool exposure does not widen them.

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
