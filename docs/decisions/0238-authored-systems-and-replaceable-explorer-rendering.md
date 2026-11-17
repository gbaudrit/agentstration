# ADR-0238: Authored systems and replaceable Explorer rendering

## Status

Proposed

## Context

[#1198](https://github.com/gbaudrit/agentstration/issues/1198) adds Console exploration
to ADR-0236/0237. A grouping needs a durable source owner; the rebuildable graph
and a visual library cannot become authoring or authorization authorities.

## Decision

The small `Systems` family owns `SystemResource` definitions and application
commands. Definitions use the existing scoped resource store, identity catalogue,
write/delete authorization and ETag concurrency boundary. Instance, Tenant and
Workspace ownership are supported without a new database or schema migration.
A definition contains a display name, optional description and at most 128 exact
UID/scope/namespace/kind/name memberships. Its stable UID survives renaming.

Membership means an authored logical relationship. It never edits an executable
resource, publication, pin or provider. Deleting a System removes that definition
and its memberships, preserving member resources. The family contributor projects
`system-membership` edges after checking the recorded identity; impact traversal
excludes them. DisplayName is semantic authored metadata, not renderer state.

Public System views filter members through current Discover authorization and
return no private totals or raw definition documents. Use alone grants no catalogue
visibility; Discover suffices for logical membership. Writes require normal scoped
permissions and current ETags. Membership edits reject a partially visible existing
set uniformly, before capacity checks. Renaming preserves unseen memberships.
Missing/recreated UIDs do not rebind by name. Cross-tenant and inaccessible
memberships fail. All routes use no-store, generic errors and request cancellation.

The Console route is `/system-explorer`. `context` selects the authorization
context, `namespace` the namespace, `scopeRef` the System owner and `root` an exact
semantic node for neighbor navigation. Renderer selection may use `renderer=list`.
Resource links retain the actor context and exact owner; the renderer cannot
construct URLs or transfer ownership. Cursor pages replace earlier observations;
the Console clears the old slice before loading another context. The initial page
contains at most 80 nodes; stale cursors require a fresh observation.

`Web.SystemExplorer` encapsulates LikeC4 1.59.4 Model Builder, browser Graphviz WASM
layout and React rendering. Builder output is computed; layout is a separate
adapter-local step. UTF-8 hexadecimal IDs preserve distinct neutral identities,
including homonyms and Unicode sequences; labels never enter model expressions.
Rendering is read-only. No resource, graph contract, endpoint or persisted document
contains positions, views, layout or styles. No graphical state is persisted.

The island uses locally bundled, locked dependencies and shadow-root styles.
Node is needed only to rebuild frontend assets, never to run the product. The
current optional module is approximately 4.4 MB uncompressed / 1.50 MB gzip,
including WASM; it is loaded on demand. Layout is bounded to 100 nodes and 20 seconds.
The accessible table, exact inspector, logical authoring and authorized JSON export
work independently with the diagram disabled or failed. Export uses the same
semantic page and never queries a renderer index. Async layout completion and
selection callbacks are rejected after disposal; React roots and WASM adapters
are released on navigation.

## Consequences

Existing SQLite/PostgreSQL resource ports retain persistence parity. No new graph
store, Node service, provider, internet or telemetry collector is required at
runtime. Renderer cost and main-thread layout remain bounded POC limitations.
The authored family deliberately provides specific membership commands instead of
wholesale replacement of a filtered document. A System is not an executable plan.

The feature owns en-US/fr-FR strings and current component/client/adapter/API and
browser smoke validation. [#1201](https://github.com/gbaudrit/agentstration/issues/1201)
remains the deferred reusable UX scenario, gated on explicit maintainer UX
validation and merged UX PR. The feature and parent remain open. Reconciliation
and live overlays remain separate verticals; Agent adoption still belongs to
[#1031](https://github.com/gbaudrit/agentstration/issues/1031).
