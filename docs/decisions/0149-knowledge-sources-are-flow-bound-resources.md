# ADR-0149: Knowledge Sources are Workspace-owned Flow-bound resources

## Status

Accepted

## Context

Agentstration needs to administer knowledge that agents can acquire and later exploit without coupling a Knowledge Source to one crawler, storage engine, MCP server, or retrieval implementation. The existing Source family distributes trusted installation content; it does not represent operational knowledge owned by a Workspace.

Acquisition and retrieval can each require arbitrary orchestration. Treating either operation as a direct extension or MCP configuration would make the control-plane resource depend on one provider mechanism and would bypass Flow composition.

## Decision

Introduce `KnowledgeSource` as a dedicated Workspace-scoped resource family owned by `Agentstration.Knowledge.Contracts`, `Agentstration.Knowledge`, and `Agentstration.Knowledge.Api`. It is persisted through the generic Management resource store and exposed from the existing API process.

A Knowledge Source has independent ingestion and retrieval Flow bindings. Each binding names a Flow in the source namespace or an explicit namespace and selects either the active published version or one exact published version. An enabled Knowledge Source requires both bindings. A disabled Knowledge Source may be retained as an incomplete draft.

Creating, updating, enabling, or provisioning a Knowledge Source validates its envelope, Workspace ownership, Flow-reference form, resolvability to published Flow versions, and the structural JSON-schema shape exposed by those versions. Validation never executes either Flow. Readiness reports the resolved versions and explains disabled, missing, or unavailable bindings.

Flow deletion is rejected while any Knowledge Source in the same Workspace references that Flow. Bootstrap profiles may provision Knowledge Sources after their published Flows, and Packs install them after Flow resources and remove them before Flow resources.

## Consequences

- Knowledge Sources remain provider-neutral; MCP, REST, AEP, storage, crawling, routing, and agent calls stay inside the referenced Flows and governed Tools.
- Active-version bindings can advance without rewriting the Knowledge Source, while exact-version bindings provide reproducibility.
- Workspace isolation, ETags, generations, namespaces, Bootstrap, Packs, and SQLite/PostgreSQL persistence reuse the existing resource-management boundaries.
- This decision establishes administration and binding readiness only. Ingestion runs, retrieval operations, ToolSet exposure, source-usage governance, and acquired-data storage are delivered by their owning follow-up verticals.
