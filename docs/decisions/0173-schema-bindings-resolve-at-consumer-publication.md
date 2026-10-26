# ADR-0173: Schema bindings resolve at consumer publication

## Status

Accepted

## Context

Flows historically store JSON Schema documents directly in their input and output contracts. Reusable Schema resources introduce scope, format and version concerns that must not make execution depend on a mutable draft, a later registry selection or the continued availability of an AEP extension.

## Decision

A consumer may declare a `SchemaBinding` with exactly one of two forms: an inline native definition with an explicit format and optional dialect, or an exact reference to an immutable published Schema version. References may specify an ownership scope and namespace; omitted values resolve to the consumer scope and namespace. Active-version and schema-to-schema composition semantics are deliberately excluded.

Flow publication resolves every graph-level and input/output-step binding in the consumer's Workspace scope. Resolution verifies the exact Schema version, descendant-use policy, required consumer format, provider availability and definition validity. A Flow currently requires the `json-schema` format because its execution validator consumes JSON Schema contracts.

The immutable Flow version stores both provenance snapshots and the effective native content. Provenance records the contract path, format, dialect, digest and, for a reference, its normalized address, source UID and source generation. The effective content is copied into the existing JSON contract fields and bindings are removed from the published graph. Runtime execution and existing Flow/Tool adapters therefore continue to consume their historical JSON shapes and perform no registry or resource lookup.

Legacy inline JSON contract fields remain accepted. Publication treats them as built-in JSON Schema bindings and snapshots them through the same validation path. Existing published versions require no migration because the added provenance property is optional.

## Consequences

- Published execution is deterministic even after a Schema draft changes or its format provider becomes unavailable.
- Cross-scope use remains fail-closed and requires the Schema's explicit descendant-use grant.
- Consumers receive stable diagnostics for missing versions, incompatible formats and unavailable providers before publication.
- Draft authoring may evolve independently while runtime contracts remain backward compatible.
- A consumer that needs a different native schema language must first gain an execution adapter for that format; binding resolution does not perform format conversion.
