# ADR-0155: Knowledge Tools use the governed internal MCP path

## Status

Accepted

## Context

Knowledge Sources already publish source-specific ToolDefinitions and ToolSets, and retrieval already runs through a shared Snapshot-bound application service. Adding Knowledge-specific MCP handlers would duplicate routing, authorization, bounds, and error behavior. Treating caller-supplied MCP metadata as Agent authority would also let a client impersonate an Agent revision.

## Decision

Knowledge `search`, `query`, and `read` Tools use the reserved `agentstration` MCP provider and the existing ToolDefinition projection. Dynamic `tools/list` exposes their public operation schemas and descriptive source/operation metadata; fixed source identity, operation, retrieval Flow, Workspace, Principal, and execution context are never accepted as model-controlled arguments. `tools/call` invokes the ordinary ToolDefinition executor, which delegates annotated Knowledge definitions to `KnowledgeRetrievalService`.

Published Agent revisions remain the authority for Agent-visible Tools. Their pinned `EffectiveToolNames` are resolved by the provider-neutral Tool catalog, so an Agent runtime is only constructed with its explicitly assigned ToolSet members. Runtime invocation uses the same internal MCP adapter locally rather than connecting back to the HTTP endpoint. The authenticated HTTP MCP endpoint remains a Principal-scoped Tool surface and does not accept caller-asserted Agent assignment metadata.

Trusted Runtime and Flow invocation context carries Agent revision, Runtime Run or parent Flow Run, Flow step, logical Tool call, physical invocation, Principal, Workspace, and correlation identifiers into the retrieval Flow input and causation fields. MCP operation receipts add exact Knowledge Source, Snapshot, artifact, retrieval Flow, and FlowRun provenance. Protocol errors remain bounded, and infrastructure failure details are available only through the correlated governed Run diagnostics.

The existing bundled documentation-search Tool remains the offline fallback until the Agentstration documentation Knowledge Source is delivered. That use-case increment must delegate or remove the fallback when it provisions the source-specific replacement; the Knowledge and MCP cores do not special-case one source name or use descriptive annotations as authority.

## Consequences

- REST, Agent, Flow, and MCP invocations reuse one retrieval application path and one set of Snapshot bounds.
- ToolSet membership stays descriptive composition until an immutable Agent revision explicitly pins members.
- Direct MCP clients cannot claim Agent identity or expand an Agent's Tool assignment through request metadata.
- Operators can correlate a Knowledge result with the exact source revision, Snapshot, artifacts, Tool call, and retrieval FlowRun without exposing backend references.
- Documentation search cutover remains part of the end-to-end documentation Knowledge Source use case rather than adding product-specific behavior to the generic Knowledge core.
