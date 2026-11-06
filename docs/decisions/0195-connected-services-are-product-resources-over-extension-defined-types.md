# ADR-0195: Connected Services are product resources over extension-defined types

## Status

Accepted

## Context

Storage resources introduced by #1032 need reusable remote-service configuration,
such as a Redis deployment or an S3-compatible account. Treating those services as
Data Sources would conflate connection ownership with governed data acquisition.
Binding OpenDAL directly to every provider extension would instead create
extension-to-extension dependencies and make lifecycle, scope and credential rules
provider-specific.

Extensions still need a way to describe provider-specific connection options,
required values and values made available to consumers. Agentstration must retain
ownership of configured instances and must never persist or return resolved Secret
values through its management API.

## Decision

Introduce the plural `ConnectedServices` resource family with dedicated contracts,
application service and API modules. A `ConnectedService` is owned by an Instance,
Tenant or Workspace scope and pins an exact Extension Registration scope, extension
identity, contribution id, contribution version and JSON-schema digest. Its
non-secret options and exact Parameter/Secret references are durable; bound values
are resolved only for an authorized internal consumer.

Extend AEP discovery with the `aep.connected-service-types` capability and
`connected-service-type` contributions. Each contribution publishes immutable
version descriptors containing a configuration schema, typed/protected inputs and
typed/protected exports. The AEP validator rejects duplicate ids, missing preferred
versions and schema digest drift. Extensions define types only; they do not own the
configured product resources.

Consumers bind explicitly to Connected Service exports by id. In particular, an
OpenDAL Storage adapter will map its own requirements to selected export ids when a
Storage is configured. Agentstration performs no name-based automatic matching and
OpenDAL does not reference a provider extension directly.

The product API exposes the type catalog and scoped Connected Service CRUD,
reconciliation and deactivation. Reconciliation observes removed or changed pinned
contracts as stale and unavailable extensions as unavailable; it never migrates a
configuration implicitly. API representations contain references and observed
state only, never resolved Parameter or Secret values.

## Consequences

- Remote service accounts become reusable, governed resources independent of Data
  Sources and Storage implementations.
- Provider extensions can add Azure, AWS, Redis or other service types without
  adding provider concepts to the product core.
- Exact revision pinning makes extension upgrades observable and requires an
  explicit operator update when a contract changes.
- Secret access remains late-bound and disposable inside the consumer boundary.
- Storage/OpenDAL integration can remain a manual product-level binding and does
  not create an extension dependency graph.
