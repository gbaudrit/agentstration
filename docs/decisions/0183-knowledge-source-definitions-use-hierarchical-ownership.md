# ADR-0183: Knowledge Source definitions use hierarchical ownership

## Status

Accepted

## Context

Knowledge Sources were owned by one Workspace even though their reusable projection graph can be shared by an Instance or Tenant. That forced duplicate definitions and prevented an ancestor-owned Agent or Flow graph from retaining one closed Knowledge dependency. Moving snapshots or acquired content to the ancestor would instead weaken the existing Workspace isolation boundary.

Logical references were also resolved again when projection started. A descendant Workspace could therefore shadow a Data Source or Flow name, or observe a changed active Data Source Profile, after the Knowledge Source had been reviewed.

## Decision

A `KnowledgeSource` definition may be owned by an Instance, Tenant, or Workspace. Its durable identity remains the exact scope, namespace, kind, name, UID, and generation. Descendant use of an ancestor Knowledge Source is default-deny and is evaluated through the shared resource-access policy overlay.

This supersedes the Workspace-only ownership portions of ADR-0149 and ADR-0167. Their separation of Data Source acquisition from Knowledge projection and their immutable Snapshot model remain in force.

Creation and update resolve the complete direct definition graph from the Knowledge Source owner scope. The resource retains:

- exact Data Source scope, namespace, name, UID, and generation;
- the exact active Data Source Profile scope, namespace, name, UID, generation, version, definition hash, and dependency hash;
- exact projection, retrieval, and optional transformation Flow scope, namespace, version, and definition hash; and
- one deterministic dependency hash over those pins.

Unqualified references that match more than one visible owner fail as ambiguous. Descendants, siblings, and cross-tenant scopes are never candidates. Protected ancestor Data Sources must authorize the scope that owns the Knowledge Source. Execution verifies every persisted pin and fails closed when a Data Source, profile revision, or Flow version is missing or stale. The execution Workspace cannot resolve the graph again by name.

Projection, acquisition, transformation, Flow Run, Artifact, Snapshot, observed active state, retrieval, index, and session data remain owned by the invoking Workspace. Operational records retain the exact Knowledge Source UID, generation, and owner scope. A Workspace must separately be authorized to use the inherited Knowledge Source and any ancestor Data Source from which it acquires content. Two authorized Workspaces therefore materialize independent operational histories from the same reusable definition.

The stored `Ready` condition describes the owner-scoped definition graph. Operational readiness is evaluated separately for the consuming Workspace so an Instance or Tenant definition never aggregates acquisition state across descendants.

The built-in Knowledge projection and retrieval Flows and their retrieval ToolSet are reconciled once at Instance scope. Development databases created before this decision must be reseeded; there is no name-based or incomplete-composition compatibility fallback.

## Consequences

- Instance and Tenant definitions can be reused without sharing acquired content or indexes.
- Data Source Profile activation and mutable Data Source changes are observable as stale pins and require a reviewed Knowledge Source update.
- Snapshots continue to preserve the exact projection and retrieval Flow evidence used by one Workspace.
- Source, Data Source, Knowledge, Flow, Tool, Pack, Runtime, and Work storage boundaries remain unchanged.
- Tool exposure is materialized in the invoking Workspace, even when the Knowledge Source definition is inherited, because ToolDefinitions and their execution state remain Workspace-owned.
- Existing local Development data must be recreated so every Knowledge Source has a complete pinned composition and built-in Knowledge definitions exist only at Instance scope.
