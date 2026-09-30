# ADR-0151: Knowledge acquisition runs published ingestion Flows

## Status

Accepted

## Context

A Knowledge Source binds an ingestion Flow, but administration alone does not provide an executable acquisition lifecycle. Acquisition must remain independent of crawler, protocol, Tool provider, storage backend, and Agent placement while retaining exact source and Flow provenance. Returning acquired documents directly would also bypass the staged and durable artifact boundaries.

## Decision

Introduce a Workspace-owned `KnowledgeAcquisition` resource in the Knowledge family. Starting an acquisition resolves the current Knowledge Source revision and its exact published ingestion Flow version, then creates a durable root FlowRun through a provider-neutral Knowledge port. The Flow must declare `knowledge.contract=knowledge.ingestion/v1`; publication validates the required input envelope and bounded artifact-manifest output shape.

The server constructs the input envelope from the source identity, bounded caller parameters, authenticated caller scope, acquisition identity, and correlation identity. Provider-specific configuration is never accepted by the acquisition API. One non-terminal acquisition is allowed per source. An optional idempotency key deterministically identifies a request, returns the same acquisition for an identical payload, and rejects reuse with different input.

Acquisition state is synchronized from the authoritative FlowRun. Cancellation delegates to that FlowRun, retry creates a new acquisition related to the terminal attempt, and history remains in the generic management resource store. A successful Flow output is normalized into at most 100 artifact references classified as intermediate, diagnostic, or publishable. The acquisition does not publish a Knowledge Snapshot.

## Consequences

- Ingestion may use governed Tools, Agents, child Flows, StagedArtifacts, and Storage Flows without adding acquisition-specific providers.
- Flow recovery, cancellation, child execution, and event idempotence remain owned by the Flow module.
- Knowledge retains queryable source revision, exact Flow version, FlowRun, caller, correlation, attempt, and manifest provenance.
- Reads and writes remain Workspace-scoped and reuse run permissions; document contents are not logged or embedded in the manifest.
- Snapshot publication, retrieval, recurring scheduling, Crawl4AI, and Console administration remain separate verticals.
