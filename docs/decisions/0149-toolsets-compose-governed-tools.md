# ADR-0149: ToolSets compose governed Tools without becoming authority

## Status

Accepted

## Context

Agents and Flows need stable collections of related Tools, including the `search`, `query`, and `read` operations of one Knowledge Source. A category is useful for discovery but cannot safely grant execution rights. Assigning a mutable collection directly to an Agent would also let a later membership change silently expand that Agent's authority.

## Decision

Introduce `ToolSet` as a Workspace-owned, namespaced resource in the Tools family. A draft names concrete Tool members and gives each member a capability and route key. Publishing creates an immutable version that pins every member's Tool identity, generation, provider mapping, schema, and approval requirement. Categories, tags, and qualified annotations remain descriptive metadata and never participate in authorization or required routing behavior.

`ToolRoute` is a provider-neutral Flow step. It references one exact ToolSet version and selects exactly one member by capability and optional route. Zero or multiple matches fail deterministically. Execution still invokes the selected Tool through the existing governed Tool pipeline, and the Flow Run records the ToolSet version plus selected Tool revision and provider provenance.

Agent definitions may assign a whole published ToolSet version or an explicit member subset. Publishing an Agent revision expands that selection into concrete Tool names and records the ToolSet definition hash and selected members. Later ToolSet versions cannot add authority to the immutable Agent revision.

`KnowledgeSourceToolExposure` is owned by the Knowledge family and maintains the typed relationship between one Knowledge Source, its exact retrieval Flow, one ToolSet version, and three Flow-backed ToolDefinitions. The exposed Tool names are source-specific. Each ToolDefinition fixes `knowledgeSourceId` and `operation`; callers may supply only the remaining public Flow input. The ToolSet remains administered only through the Tools API.

## Consequences

- Tool remains the unit of execution, authorization, approval, and audit; a ToolSet is never executable.
- MCP and AEP remain Tool provider types. ToolSet routing is provider-neutral and does not introduce another provider mechanism.
- Published ToolSet versions detect missing or changed Tool revisions instead of silently retargeting them.
- Knowledge retrieval behavior remains implemented by its Flow; exposure only provides a governed source-specific Tool surface.
- Bootstrap and Pack handlers can distribute ToolSets before Agents that consume them.
