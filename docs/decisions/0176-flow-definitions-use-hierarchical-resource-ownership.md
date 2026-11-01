# ADR-0176: Flow definitions use hierarchical resource ownership

## Status

Accepted

## Context

Flow definitions and their immutable published versions were originally keyed only by `WorkspaceId`. That model prevents an Instance or Tenant administrator from publishing a reusable Flow while retaining one authoritative owner, and it cannot express which ancestor-owned Flow a Workspace Run actually executed.

The shared resource model already represents exact Instance, Tenant, and Workspace ownership with `ResourceScopeRef`. ADR-0105 defines ancestor-only visibility for governed resources. Flow execution data has different isolation requirements: drafts, Runs, input requests, events, checkpoints, and runtime state belong to the consuming Workspace and must not become ancestor-owned merely because the selected definition is inherited.

## Decision

### Definition ownership and identity

A Flow definition and every published Flow version carry one exact `ResourceScopeRef`. Their durable identity is the tuple `(scope, namespace, flow id)` and `(scope, namespace, flow id, version)` respectively. The same namespace, id, and version may therefore exist independently at Instance, Tenant, and Workspace scope.

Create, read, update, delete, publish, activate, and exact-reference operations preserve that exact owner scope. Storage implementations for SQLite and PostgreSQL must provide equivalent exact-scope behavior while Flow remains the owner of its repository and schema.

### Visibility and references

A consumer may resolve only its own scope and its visible ancestors. Workspace consumers can see Workspace, their Tenant, and Instance definitions; Tenant consumers can see their Tenant and Instance definitions; Instance consumers can see only Instance definitions. Siblings and descendants are never candidates.

An explicit scope on a Flow reference selects that exact visible owner. An unqualified reference is resolved deterministically across the consumer's visible scope chain. If more than one visible definition has the same logical identity and no exact scope was supplied, resolution fails closed instead of silently selecting a shadowing definition. This keeps portable manifests deterministic and makes ownership changes observable.

### Execution isolation and snapshots

Flow drafts remain Workspace-owned authoring state. Flow Runs, input requests, events, checkpoints, child-run coordination, and runtime state remain Workspace-isolated even when their definition is inherited from Tenant or Instance scope.

Before execution, Agentstration resolves the definition and pins its exact owner scope, namespace, id, immutable version, and content hash in the execution snapshot. Runtime Workers receive only that pinned material and do not repeat hierarchical resolution. This refines the Flow execution coordinates established by ADR-0168 without changing AWP placement or authority.

### Bootstrap and managed resources

Bootstrap may create Flow definitions at Instance, Tenant, or Workspace scope subject to the existing scope policy and target validation. Pack- or Extension-managed ownership remains orthogonal: management provenance can make a Flow immutable to ordinary mutation, but it does not replace the exact resource scope or allow a provider to govern resources outside its declared scope.

## Consequences

- Flow APIs and Console surfaces must distinguish exact ownership from inherited visibility and must not offer edit or delete actions for an inherited definition.
- Flow references and execution material must retain the resolved scope; a version string alone is insufficient.
- Workspace-only compatibility methods may remain as wrappers over exact Workspace scope while callers migrate.
- Storage initialization performs an idempotent compatibility migration for persisted Workspace Flow definitions and versions because both their serialized ownership field and durable key predate `ResourceScopeRef`; conflicting old and new identities fail startup rather than overwrite data.
- Cross-scope authorization, deterministic ambiguity errors, bootstrap coverage, storage parity, and run-snapshot pinning require dedicated tests.
- This decision supersedes the Workspace-only ownership portions of ADR-0053 and ADR-0080. Their Flow domain, immutable publication, and Workspace execution-state decisions remain in force. ADR-0105 remains authoritative for the general visibility hierarchy.
