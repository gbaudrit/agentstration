# ADR-0236: SystemGraph is an authorized rebuildable semantic projection

## Status

Proposed

## Context

[#1196](https://github.com/gbaudrit/agentstration/issues/1196), a child of #1195,
needs a cross-family metadata API without transferring lifecycle authority from
families. The maintainer approved this first API vertical on 2026-11-16.
Its baseline is `integration/fr-1150` at
`0b76d6ca81643a7577b565f07fd7dd43087b4c06`, containing `integration/fr-1032` at
`0c6a38632e395d230d56de71907f0b7c9a6acf37`. Their PRs remain independent.

## Decision

### Authority, identities and versions

Family stores remain authoritative for definitions, publications, grants and
lifecycle. SystemGraph owns a disposable semantic projection, no resource
definitions, execution state or renderer state. Contracts depend only on neutral
Resources primitives. Application queries and HTTP have separate boundaries.

Logical address `(scope, namespace, kind, name)`, UID, generation, ETag, immutable
publication and hash have separate meanings. Definition nodes use resource UID;
recorded-generation nodes use the actual dependency UID plus generation; Agent
revision nodes use their native revision UID. Flow publication nodes combine the
owning Flow UID and exact version because FlowVersion has no independent UID.
Graph IDs are deterministic derived keys, not replacement family identifiers.

A recorded profile generation is evidence from an immutable consumer, not a
retained executable copy of that profile. Missing or recreated dependencies never
bind to a replacement with the same name. Historical publications are unchanged.
An active-version pointer does not change publication identity.

### Contributors and resolution

Agents, Models and Flows extract typed family metadata through explicit
contributors. Resource Management supplies typed reading helpers; Identity and
its persisted scope resolver supply hierarchy. The initial cohort covers
Model/Runtime Profiles, Agent definitions/revisions and Flow definitions/versions.

Agent declarations reuse the existing owner-relative reference resolver. Exact
edges read ModelProfilePin, RuntimeProfilePin and recorded Agent revision UIDs.
Inspection never calls the publication-time Flow Agent resolver, which can create
a revision. Missing optional pins remain unknown. No arbitrary JSON scan or
independent resolver is introduced.

Declared references, publication membership, generations and exact dependencies
have separate relation kinds. Source field paths explain authorized bindings.
Governance exposes the source's origin kind only; manager references potentially
identifying other resources are omitted.

### Authorization

The application composes current resource-read RBAC, exact administrative context
and existing Discover/Use policies. It selects readable source scopes and their
ancestors before opening a metadata-only trusted reading scope. The internal scope
is closed before rechecking the request context and has no HTTP index surface.

Each node is checked against its current exact family identity. Agent revisions
inherit catalogue and binding visibility from their exact owning Agent UID.
Discover permits catalogue identity; binding evidence and relations also require
Use. Same-scope access requires ordinary RBAC. Use-only creates no visibility.
Both relation endpoints must be currently visible and inspectable. Missing and
invisible neighbour roots return the same empty result.

Only authorized, namespace-selected metadata enters snapshot hashes, pagination
and counts. No privileged totals, hidden degrees or raw contributor diagnostics
are returned. Page scope hierarchy includes only scopes represented in that page.
Grant-only changes do not mutate source definitions, hashes or publications.

### Reconstruction, consistency and limits

Every request rebuilds from existing stores twice and rejects different
authorized observations or a changed request context. Contributor failure rejects
the query with a generic error, without partial results or exception details.
Cancellation propagates. There is no persistent index or incremental cache.

Independent stores have no shared transaction. Repeated-read equality is an
observation check, not a globally atomic snapshot or a guarantee against every
concurrent change. Responses explicitly state that they are not globally complete.
A visible slice cannot certify absence, compatibility or reconciliation safety.

Pages contain 1–200 nodes, default 100. Authorized reconstruction permits at most
10,000 nodes and 20,000 relations. A page includes only edges with both endpoints
in that page. Neighbour queries require at least two slots and retain their root
in every page. HMAC-authenticated cursors bind the principal/context, query and
authorized snapshot and expire after five minutes. Process restart invalidates
them. Tampering, expiration and changed query/projection fail uniformly.

### Local operation and extension boundaries

Owner scope, consumer authorization, exact invocation scope and Worker placement
remain distinct. Inspection starts no run/acquisition, issues no content lease,
creates no resource/grant/publication and opens no provider session or Secret.
Existing policy evaluation may record security audit decisions. SQLite and
in-process deterministic execution suffice; optional stores are reused unchanged.

No layout, coordinates, viewport, style, visual grouping or LikeC4 field or
dependency belongs in SystemGraph. #1197 extends dependency coverage/impact;
#1198 owns modelling/rendering; #1199 owns reconciliation; #1200 owns the separate
operational overlay. No empty extension projects are introduced.

## Consequences

The API is executable without a renderer or live provider. Source history can be
projected only while its family retains the evidence; a graph rebuild cannot
restore purged family history.

The DAT's mandatory operational-Workspace descriptions differ from exact
FlowRunScope/FlowRunKey contracts, their tests and historical ADR-0180 wording.
ADR-0185 records a different placement decision. This discrepancy remains visible:
this vertical rewrites no accepted ADR and changes no execution storage or
placement, nor does it normalize exact invocation identity to Workspace-only.

Flow version enumeration uses native bounded pages in both SQLite and PostgreSQL.
Each authorized observation collects nodes first, then scans relations and retains
only those with authorized endpoints before applying the relation budget. Hidden
relations cannot exhaust that public budget. The second observation repeats both
phases; no raw cross-family edge index is retained.
