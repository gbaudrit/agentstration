# ADR-0174: Agent and Tool Schema bindings preserve runtime protocols

## Status

Accepted

## Context

Reusable Schema resources already resolve into immutable Flow contracts. Agents and Agentstration-owned ToolDefinitions also expose contracts to Flow authors and model tool calling, but their execution boundaries differ: the provider-neutral Agent runtime exchanges text, while Tools expose MCP-owned JSON schemas. Treating a reusable Schema as a new runtime payload protocol would leak Management resources into execution and break existing adapters. Replacing schemas discovered from external MCP or AEP providers would also make Agentstration contradict the provider's authoritative contract.

## Decision

Editable Agent and ToolDefinition resources may select either an inline JSON Schema binding or one exact published reusable Schema version for input and output. Saving resolves the binding in the consumer scope, validates the built-in `json-schema` format, and retains both the binding and its effective content.

Creating an immutable Agent revision snapshots the effective contracts and their provenance. The contracts contribute to the revision definition hash, but `AgentExecutionRequest` and `AgentExecutionResult` remain text-based; structured values continue to be encoded in those existing values. No runtime adapter resolves Schema resources.

Saving a ToolDefinition resolves its bindings before comparing them with the published backing Flow. The effective schemas are materialized into the ordinary governed Tool resource, so MCP publication, Agent assignment, validation, and execution keep using their existing JSON contracts.

Schemas discovered from external MCP or AEP Tool providers remain authoritative and read-only. Agentstration may inspect or derive authoring assistance from them but does not attach local bindings or mutate their provider-owned shape.

## Consequences

- Agent and Tool authoring can reuse exact governed contracts without creating another execution protocol.
- Immutable Agent revisions and materialized Tools remain executable when a Schema draft or provider later changes.
- Flow-backed ToolDefinitions still have to match their published Flow contract after binding resolution.
- External Tool discovery keeps one contract authority and cannot be silently overridden by local authoring.
