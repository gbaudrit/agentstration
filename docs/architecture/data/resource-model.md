# Resource model

The Control Plane uses a small Agentstration-native declarative envelope: `uid`, `apiVersion`, `kind`, `metadata`, and a kind-specific `definition`. Each resource family owns its validation and lifecycle; the server generates immutable UIDs and persists desired state behind provider-neutral ports. An immutable `ScopeId` associates each persisted resource with the canonical instance, Tenant, or Workspace scope hierarchy without duplicating scope columns in the public envelope.

Agent definitions, revisions, and deployments remain separate durable concepts. An Agent definition directly declares its handler, instructions, model profile, Tools, middleware, behaviors, context providers, and settings. Publication produces an immutable resolved revision with a deterministic definition hash. Deployment records desired, provisioning, operational, and observed revision state separately. Runtime objects are reconstructed from that durable chain and are never persisted.

The [interactive L3 view](../interactive.mdx) owns the structural relationship between resource-family services, storage, Runtime, and reconciliation. This page owns the data invariants rather than a second structural diagram.

`apiVersion` selects the schema and currently equals `agentstration.io/v1`. Exact logical identity is `(scope, namespace, kind, name)`. References use `name` plus optional `scopeRef` and `namespace`; omitted scope uses the authorized current scope, while an omitted namespace inherits the owner's namespace. Reference resolution never grants cross-scope use by itself. ETags protect concurrent mutations. See the [resource reference](../../reference/resources/overview.md) and [versioning strategy](../../reference/versioning.md).
