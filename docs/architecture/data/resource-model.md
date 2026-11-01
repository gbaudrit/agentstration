# Resource model

The Control Plane uses a small Agentstration-native declarative envelope: `uid`, `apiVersion`, `kind`, `metadata`, and a kind-specific `definition`. Each resource family owns its validation and lifecycle; the server generates immutable UIDs and persists desired state behind provider-neutral ports. An immutable `ScopeId` associates each persisted resource with the canonical instance, Tenant, or Workspace scope hierarchy without duplicating scope columns in the public envelope.

Agent definitions, revisions, and deployments remain separate durable concepts. An Agent definition directly declares its handler, instructions, model profile, Tools, middleware, behaviors, context providers, and settings. Publication produces an immutable resolved revision with a deterministic definition hash. Deployment records desired, provisioning, operational, and observed revision state separately. Runtime objects are reconstructed from that durable chain and are never persisted.

The [interactive L3 view](../interactive.mdx) owns the structural relationship between resource-family services, storage, Runtime, and reconciliation. This page owns the data invariants rather than a second structural diagram.

`apiVersion` selects the schema and currently equals `agentstration.io/v1`. Exact logical identity is `(scope, namespace, kind, name)`. References use `name` plus optional `scopeRef` and `namespace`; omitted scope uses the authorized current scope, while an omitted namespace inherits the owner's namespace. Reference resolution never grants cross-scope use by itself. ETags protect concurrent mutations. See the [resource reference](../../reference/resources/overview.md) and [versioning strategy](../../reference/versioning.md).

## Implemented scope matrix

| Resource responsibility | Allowed owner scope | Operational-state scope |
| --- | --- | --- |
| Model Provider, discovered Model, Runtime Profile, Source Registry registration and observations | Instance | Provider observations remain attached to the exact Instance resource. |
| Model Profile, Agent and immutable Agent revision, Flow and immutable Flow version, Tool Provider, Tool, ToolSet and immutable ToolSet version, Data Source and profile revisions, Knowledge Source, Source resources, Schema, Parameter, Vault, Secret, installed Pack, and Extension registration | Instance, Tenant, or Workspace according to the family policy | Invocations and family-specific operational records remain in the consuming Workspace, including a protected operational Workspace for Instance or Tenant administration. |
| Trigger, Agent Deployment, Tool Definition, Tool Category, Tool execution hook, Data Source discovery/acquisition target, Pack Project, and Pack build | Workspace | The same exact Workspace. Administrative Trigger state uses the protected operational Workspace selected by the logical Instance or Tenant context. |
| Work Items, Flow Runs, Runtime Runs, conversations, sessions, assignments, input requests, artifacts, Knowledge Snapshots, acquisitions, projections, and retrieval state | Not reusable definitions | Business Workspace or protected operational Workspace. These records are never inherited by descendants. |

An owner-scoped reusable resource may depend only on an exact same-scope resource or an explicitly authorized ancestor resource. `discover` controls descendant catalogue visibility and `use` controls functional resolution; neither action changes ownership. Publication pins the exact owner scope, UID, namespace, generation or immutable version, and integrity evidence required by that family. The later execution context cannot rebind those dependencies by name.

Definition ownership, administrative context, authorization scope, operational placement, principal ownership, and visibility are therefore independent coordinates. Their implementation-backed interpretation is detailed in the [hierarchical resource-access reconciliation](../implementation/hierarchical-resource-access-reconciliation-2026-10-26.md).
