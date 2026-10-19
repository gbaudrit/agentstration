# ADR-0166: Flow contracts use one Flow-owned metadata key

## Status

Accepted

## Context

Several resource families classify published Flows by a stable contract value. Artifacts used `artifact.contract`, Knowledge used `knowledge.contract`, and Data Sources used `dataSource.contract`. The keys described the same Flow-level concept, forced generic Flow consumers to know family-specific metadata names, and made composition across acquisition, transformation, projection, storage, and retrieval unnecessarily irregular.

The contract values remain meaningful to their owning families. For example, Artifacts owns `artifact.storage.write/v1`, Data Sources owns `datasource.acquisition/v1`, and Knowledge owns `knowledge.projection/v1`. The metadata location that carries one such value belongs to the Flow resource itself.

## Decision

All classified Flows declare their contract through the single `flow.contract` metadata key. The Flow family owns that key and exposes it as `FlowMetadataKeys.Contract`. Resource families continue to own, validate, and version their contract values. An activation guard inspects only the contract-value prefix owned by its family, ignores values owned by other families, and rejects unsupported values within its own prefix.

Current contract values include:

- `artifact.storage.write/v1`, `artifact.storage.read/v1`, and `artifact.transform/v1` for Artifact operations;
- `datasource.acquisition/v1` for Data Source acquisition;
- `knowledge.ingestion/v1`, `knowledge.projection/v1`, and `knowledge.retrieval/v1` for Knowledge processing.

Additional metadata that qualifies one contract remains family-owned. In particular, `knowledge.capabilities` continues to declare the operations supported by a `knowledge.retrieval/v1` Flow.

The previous `artifact.contract`, `dataSource.contract`, and `knowledge.contract` keys are removed without aliases or dual reads. These contracts have not shipped from the integration branch, so existing local development data must be reset or reconciled through normal built-in provisioning rather than treated as a production migration.

This decision supersedes the metadata-key conventions implicit in ADR-0151, ADR-0152, ADR-0154, and ADR-0164. It also replaces ADR-0164's pre-release `data.source.acquisition/v1` spelling with `datasource.acquisition/v1`. Their domain ownership, execution, validation, and provenance decisions remain unchanged.

## Consequences

- Flow tooling can discover a contract through one stable key without importing every resource family.
- Resource families remain responsible for interpreting and validating their own contract values and schemas.
- A Flow declares at most one primary contract value; further capability metadata does not create another primary contract.
- Bootstrap manifests, tests, and development data must use `flow.contract` exclusively.
