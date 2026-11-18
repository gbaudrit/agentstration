# ADR-0228: Data Source queries evolve independently from Tool exposures

## Status

Accepted

## Context

The acquisition document access introduced by ADR-0227 separates retained
publications from mutable Storage documents. A Tool which embeds query JSON in
its immutable version couples filter changes to Tool and Flow republication.
FR #1179 requires named queries with their own publications, a generic executor,
and independently named relay exposures. It also addresses the missing Tool input
for free-form JSON and the loss of publication links after navigation.

## Decision

Data Sources owns `DataSourceQuery` drafts and immutable
`DataSourceQueryVersion` publications, persisted through the existing
`IResourceStore` JSON resources. Both support explicit Instance/Tenant/Workspace
ownership and namespaces. No new database table, provider protocol or Schema
lifecycle is introduced. A draft retains its Data Source UID, acquisition stream,
query profile/model and adopted Schema contract evidence alongside bounded query
JSON. The source identity and stream cannot change on that Query identity.

Publishing snapshots a reviewed draft ETag and records its exact definition,
contract digest, Query UID/generation and publication time. Draft edits do not
change published versions. Latest means the most recently published version,
including when a lower semantic version is published later; drafts and active
version pointers are excluded. Per-Query publication serialization and monotonic
publication timestamps provide deterministic order in the authoritative process.
Inventories are bounded and fail explicitly beyond their limits.

A versioned generic `datasource-query` Tool delegates to the internal query
executor. It accepts an exact scoped Query identity and an optional Query version.
An independently named relay fixes that Query identity/UID and optionally its
version. It fixes the exposed query-contract digest rather than copying the query
JSON. Both use existing ToolDefinition relay/publication machinery, governance
and authorization. Administrative execution retains the existing requirement for
a published ToolDefinition; raw native Tools do not gain administrative access.

An unpinned relay resolves the latest Query publication at each initial call.
Compatible filter/order/paging changes therefore need no Tool or Flow
republication. The contract includes Source/stream identity, exact profile/model,
retained Schema evidence and result-shaping query options. For the existing OData
profile, `$filter`, `$orderby`, `$top` and `$skip` do not change that contract;
other options are conservatively included. Unknown profiles retain all options in
the contract. Projection or contract changes require explicit Tool adoption.
An incompatible latest publication fails; there is no fallback to an older one.
Definition validation bounds JSON and verifies the adopted metadata contract;
provider query-language validation still applies at execution without executing
business queries during publication.

Current Query discovery and Use, Source Use/enabled identity, runs/read,
runs/execute and Storage/provider rights are enforced at their existing boundaries.
resources/write is checked for draft and publication operations, including
administrative contexts. Version reads use a trusted exact/bounded lookup only
after authorization of their owning Query, rather than requiring independent
access grants for each immutable version. Query lifecycle and successful execution
produce security audit events without query bodies. Runtime results retain the
actual Query/version UID, definition/contract digests and acquisition evidence.

A protected Query continuation wraps the existing acquisition cursor and binds
the original Query reference, expected contract and acquisition configuration.
It fixes the actual published Query version and delegates acquisition/actor/
context/lease/expiry checks to ADR-0227's document access boundary. New Query or
acquisition publications cannot change an in-progress continuation. Changing
arguments or tampering is refused.

## Consequences

- Content tests ad hoc queries and saves named drafts. Queries edits, publishes and
  executes saved definitions. Tools reloads persisted generic, Query relay and
  existing direct acquisition publications after navigation or refresh.
- Query and Tool names reuse shared display/technical-name fields. Publishing an
  unrelated Tool under an existing name is refused.
- Storage administration retains collection configuration, contracts, permissions
  and capabilities. Its mutable query/Tool-publication block is removed; document
  APIs remain available. This evolves the Console placement in ADR-0227.
- Existing direct acquisition-query Tool versions and the native document handler
  remain readable/executable. They are identified as existing direct exposures;
  no immutable Tool or Flow is rewritten automatically.
- Nested free-form JSON requires raw JSON editing instead of an empty generated
  heading. Tool failures retain their bounded Data Source error code and a
  localized explanation.
- The independent publication model adds resource kinds and API operations, with
  additive configuration persistence and no historical backfill. Generic Tool
  creation uses existing scoped Tool publication; name conflicts fail explicitly.
- Required component/API/persistence tests and existing browser regressions run in
  the implementation PR. The new reusable browser scenario #1180 remains blocked
  on maintainer UX validation and merge; #1173 retains the broader Schema journey.
