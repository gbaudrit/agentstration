# ADR-0211: Resources bind ordered Flows to typed composition points

## Status

Proposed

## Context

Feature [#1117](https://github.com/gbaudrit/agentstration/issues/1117) needs configurable
transformations within standard and custom source orchestrations. A single special
transformation slot cannot represent independent input sequences and a sequence
over the aggregated Knowledge result. Published Flow versions and already accepted
executions must remain immutable.

## Decision

A published graph may declare a `composition` step with a unique logical `point`,
explicit input and output JSON Schemas, an input mapping and `allowEmpty`. Resource
configuration supplies an ordered list of published child Flow references and
literal JSON parameters for each declared point. Each ordinary child receives
`{ value, parameters }`; its successful result becomes the next value. An empty
point is identity only when its input is assignable to its output. Contracts use a
bounded schema profile and conservative structural assignability; unsupported
validation keywords fail closed. Parameters are literals, including strings that
look like Flow expressions.

At resource validation and execution admission, the Flow application service resolves
scope, namespace, enabled state, publication, resource UID, exact version, hash and
Use permission. It rejects unknown points, missing required bindings, incompatible
chains, ambiguous success outputs, recursion and excessive depth or sequence size.
Configured calls expand to ordinary governed Flow-call steps followed by a transform
with the original point identity. Fixed surrounding steps remain in the executable
graph. The standard child execution, causality, cancellation and retry boundaries
continue to apply. Source acquisition and projection boundaries are server-owned,
required, and must occur on every successful path in a custom orchestration.

The accepted Flow Run stores the expanded definition snapshot and executable hash.
A composition receipt stores the published template hash and the exact accepted
bindings. The published template is neither modified nor republished for every
configuration. Recovery uses accepted pins and parameters; a subsequent configuration
change cannot rewrite accepted work. Reusing a root invocation identity with another
composition is rejected. Dispatch rechecks the selected child UID, hash and Use
permission, without resolving its active version again.

Native Data Sources optionally use `data-source-orchestration-builtin`: the fixed
operation acquisition point followed by `transformations`, then source-owned Storage
publication of the final validated value. The complete sequence is one acquisition.
An operation child is authorized only as a genuine descendant of that accepted
acquisition, with the exact native adapter Flow and unchanged operation envelope.

Knowledge prepares independent named inputs, applies each binding's optional sequence
through `content-transformation-sequence-builtin`, aggregates them through the selected
projection Flow, and optionally applies `postAggregate` in
`knowledge-orchestration-builtin`. Knowledge sequence values are Artifact manifests;
ordinary native dataset sequence values are JSON objects. Input sequence receipts and
root receipts remain in projection and Snapshot provenance. Optional input failures
remain explicit input issues; required input or final sequence failures prevent
publication and preserve the previous active Snapshot.

Existing resources with no new composition configuration keep their historical Flow
path. Existing single `transformationFlow` and the new sequence cannot both be set on
one binding; the server rejects the ambiguity. Existing immutable publications,
acquisitions and Snapshots are not migrated or rewritten.

## Consequences

- The Designer owns point authoring; source editors select, order and parameterize
  child Flows and expose the points declared by the selected orchestration.
- Native Knowledge read/search/status remain independent of retrieval Flows (ADR-0210).
- No vector search, conditional configurable sequences or parallel configurable calls
  are introduced. A custom orchestration can retain its fixed graph behavior.
- Source contracts reference provider-neutral Flow contracts. Infrastructure implements
  the source-to-Flow resolution port; source modules do not reference Flow application
  services or concrete storage providers.
- Payload freshness and retention remain owned by #1115. The separate reusable UX
  coverage is tracked by [#1118](https://github.com/gbaudrit/agentstration/issues/1118).
