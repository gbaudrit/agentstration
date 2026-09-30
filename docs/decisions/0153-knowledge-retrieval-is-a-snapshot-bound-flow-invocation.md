# ADR-0153: Knowledge retrieval is a Snapshot-bound Flow invocation

## Status

Accepted

## Context

Knowledge Snapshots provide immutable acquired content, but consumers still need search, query, and read without coupling Agentstration to one index, RAG implementation, model, or transport. Invoking the bound Flow with caller-controlled source, operation, or artifact references would bypass source-specific Tool authorization and Snapshot isolation.

## Decision

Define `knowledge.retrieval/v1` as the shared retrieval Flow contract. A published retrieval Flow declares one or more comma-separated capabilities under `knowledge.capabilities`: `knowledge.search/v1`, `knowledge.query/v1`, and `knowledge.read/v1`. Activation validates the shared input envelope and bounded output envelope before the version can become active.

`KnowledgeRetrievalService` is the common application boundary for REST and source-specific Tool invocation. It authorizes execution, resolves the current KnowledgeSource revision, pins the bound published Flow version, selects the active or an explicitly named immutable Knowledge Snapshot, and injects the source identity, operation, Snapshot evidence, caller, correlation, and retrieval identity. Caller input contains only the operation request and optional Snapshot selection; it cannot replace the source or operation fixed by the route or Tool.

The exact retrieval Flow executes as a durable FlowRun and may compose child Flows, governed Tools, Agents, indexes, APIs, and `artifact.storage.read/v1` Storage Flows. The core does not prescribe a retrieval technique. Search, query, and read results are bounded, and every returned item and citation is validated against the selected Snapshot. Read additionally rejects ranges and artifacts outside that Snapshot.

Source-specific ToolDefinitions remain the governed, assignable units published in their ToolSet. Their Knowledge annotations route execution through `KnowledgeRetrievalService` rather than passing placeholder system arguments directly to the retrieval Flow. REST exposes the same search, query, and read service through the Knowledge API. MCP transport remains a later adapter over this boundary.

## Consequences

- Each invocation records exact source, Snapshot, Flow version, FlowRun, caller, and correlation provenance without adding a second retrieval-run store.
- A Flow may implement any subset of search, query, and read; Tool exposure publishes only declared capabilities.
- Active Snapshot changes affect later invocations, while an explicit historical Snapshot remains deterministic.
- Cross-Workspace and cross-Snapshot access fail before content is returned, and Flow outputs cannot cite artifacts outside the selected Snapshot.
- Retrieval remains compatible with deterministic local Flows and optional Agent-assisted or provider-backed implementations.
- Global cross-source search, MCP exposure, indexing policy, and Console administration remain separate increments.
