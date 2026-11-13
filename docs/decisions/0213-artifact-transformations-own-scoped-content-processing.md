# ADR-0213: Artifact transformations own scoped content processing

## Status

Proposed

## Context

Feature [#1144](https://github.com/gbaudrit/agentstration/issues/1144), a child of
[#1117](https://github.com/gbaudrit/agentstration/issues/1117), needs to transform
stored content through ordinary Agents, Tools and Flows. A JSON mapping does not
read an Artifact. Requiring a separately published processor or modifying generic
Tool contracts would unnecessarily restrict reuse. Large content must remain
accessible without placing the entire document in every model message.

## Decision

Introduce `artifactTransform`, separate from `transform`. Its `process` transition
enters a private processing region authored in the same graph. A successful return
to the owning step ends that region. `completed` and `error` are ordinary outer
continuations. Processing can use existing Agent, Tool, Flow, mapping and condition
steps. It needs no independently published Flow or nested transformation step.

Validate boundaries, one unconditional processing entry, a result return, ordinary
error paths, acyclicity and bounded counts. The authority extracts the accepted
region into a private child execution snapshot for each ordered Artifact. That
snapshot keeps the published parent immutable, uses existing child causality,
lease fencing, descendant limits and cancellation, and cannot be redirected by
worker-provided child definitions. This child is an execution detail, not a new
resource that an author must publish.

Invocation adaptation belongs to the owning step: `artifactsMapping`,
`processingInputMapping` and `resultMapping`. A content invocation reads bounded
UTF-8 text or JSON and maps it into an unchanged processor contract. Its selected
result becomes output content. An access invocation passes metadata and logical
input/output handles. The processing chooses its own reading strategy.

The authority binds input reads and searches to one exact authorized Artifact and
its digest. Temporary content Tools are added to Agent execution material only
inside an access processing context; Agent resources and revisions are unchanged.
Ordinary governed content Tools are also available to authored Tool steps. They
derive the context from trusted invocation coordinates, not a caller-selected
Artifact ID, Storage reference or credential. Child Flows inherit the context.
Access ends on expiry, owner termination, attempt change or completed publication.
Existing unrelated Agent Tools retain their own authorization; these scoped Tools
do not revoke broader grants or imply that an arbitrary Agent is confined.

Retained execution stores its output draft through the existing staging subsystem. Every successful
write creates a sealed revision that can be read and searched. Writes require an
expected revision and a replay identifier; a changed replay is refused. Corrections
invalidate a previous completion declaration. Cursors bind the session, input or
output side, revision and UTF-16 position. Search matches use that same range unit.
An explicit completion selects a technically valid output revision. It does not
prove anonymization quality.

Direct execution under ADR-0212 instead keeps input and output drafts in the
existing bounded process-local handoff cache, sharing its admission budget and
owner lifetime. It performs no intermediate Artifact creation or document-bearing
runtime state persistence. Scoped content operations have the same revision,
replay and cursor rules. The owning step returns a bounded inline manifest; only
final Snapshot publication writes retained content. Expired, aborted or cold
execution fails without implicit reacquisition or a Storage fallback.

All processing results are prepared before any output Artifact is registered.
Only the owning step persists and catalogues the final immutable outputs through
the ordinary Artifact durable store and its selected local default Storage. The
receipt retains the producing Flow/step, processing child, input Artifact/digest
and materialization evidence. Source publication remains owned by the source;
a failed processing region cannot replace its active Snapshot. Storage or catalog
failure during finalization can leave retained orphan outputs; no cross-module or
distributed transaction guarantee is introduced.

## Consequences

- Existing JSON mapping and source composition contracts remain compatible.
- The Designer exposes explicit content/access modes and invocation mappings.
- Processing is sequential, bounded to 128 Artifacts; the default is 32.
- Content mode defaults to 64 KiB and accepts at most 1 MiB. Access mode supports
  UTF-8 inputs up to 16 MiB without whole-document model input. Existing provider
  and publication limits can impose lower limits.
- Output defaults to 8 MiB, at most 16 MiB, with at most 128 writes per Artifact.
  Reads return at most 16,384 UTF-16 characters. Regex patterns and returned
  matches are bounded and execution has a timeout.
- Content access expires after one hour. Staging retention bounds abandoned and
  superseded revisions. State persistence holds metadata and references, not
  document contents. A partially written unsealed revision fails closed.
- Binary extraction, OCR, automatic semantic chunking and vector search are not
  part of this decision. Direct ephemeral acquisition continues to refuse
  intermediate Artifact creation under ADR-0212; its processing drafts remain
  in memory and current inline publication limits still apply.
- Deferred reusable Designer/browser coverage is tracked by native child
  [#1145](https://github.com/gbaudrit/agentstration/issues/1145), after maintainer UX
  validation and implementation merge.
