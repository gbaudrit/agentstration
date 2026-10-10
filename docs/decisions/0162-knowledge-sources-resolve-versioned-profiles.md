# ADR-0162: Knowledge Sources resolve reusable versioned profiles

## Status

Accepted

## Context

ADR-0149 bound every Knowledge Source directly to ingestion and retrieval Flows. That keeps the source provider-neutral, but repeats the same Flow and governed Tool composition across every source. Replacing a crawler or retrieval implementation then requires editing each source independently and provides no single reviewable activation point.

Extensions may contribute specialized compositions, while operators also need stable logical profiles such as `web` whose implementation can be changed without changing the identity of every source. Accepted acquisitions, snapshots, and retrieval runs must remain reproducible after such a change.

## Decision

Introduce the Workspace-owned `KnowledgeSourceProfile` resource and immutable `KnowledgeSourceProfileRevision` publications. A profile draft owns the source-configuration JSON schema, ingestion and retrieval Flows, optional Storage Flows, direct governed Tool bindings, exact ToolSet routes, limits, and policies. Publication resolves and pins exact Flow, Tool, Tool Provider, and ToolSet provenance. One published revision may be activated, and an older revision may be reactivated as an explicit rollback.

A Knowledge Source references one logical profile and retains only its source-specific governed configuration. New acquisition and retrieval work resolves the currently active profile revision and pins that complete resolution in the acquisition, Snapshot, and retrieval provenance. A later profile activation affects new work without rewriting sources or changing the provenance of accepted work.

Applying a provider or extension profile to another logical profile copies its composition into the target draft while preserving the target identity. The operation first previews dependency and source-configuration impacts, may apply explicit configuration defaults, records source-profile provenance, and publishes a new immutable target revision. It never grants authority through descriptive metadata.

Legacy Knowledge Sources with direct ingestion and retrieval Flow bindings remain executable during migration. A source cannot combine a profile reference with direct Flow bindings.

## Consequences

- Many sources can share one governed composition and move together when an operator activates a new profile revision.
- Extensions can ship specialized profiles without forcing existing sources to adopt extension-owned identities.
- Source configuration remains data; executable authority stays in exact published Flow, Tool, provider, and ToolSet dependencies.
- Bootstrap and Packs order profiles after their dependencies and before Knowledge Sources.
- Flow deletion is rejected while a profile draft or immutable profile revision retains it.
- ADR-0149 remains valid for Workspace ownership and provider neutrality, but its direct per-source Flow binding is now the compatibility path rather than the preferred model.
