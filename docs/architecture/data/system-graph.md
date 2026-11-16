# Semantic SystemGraph

SystemGraph is the read-only cross-family metadata projection introduced by
[#1196](https://github.com/gbaudrit/agentstration/issues/1196).
[ADR-0236](../../decisions/0236-system-graph-is-an-authorized-rebuildable-semantic-projection.md)
records authority, identities, contributors, authorization and consistency.
Families retain ownership of definitions, publications, grants and lifecycle.

## Local API

`GET /api/system-graph` requires `resources/read` and uses the authenticated exact
Console/API context. It accepts:

| Parameter | Meaning |
| --- | --- |
| `namespace` | One namespace; omitted means all namespaces in the authorized slice. |
| `root` | A returned graph node ID; selects that node and its visible direct neighbours in either direction. |
| `limit` | Page size from 1 to 200, default 100; neighbour queries require at least 2. |
| `cursor` | Opaque continuation for the same principal, context, query and snapshot. |

Responses include nodes, typed relations, page scope hierarchy, authorized snapshot
digest, observation time, coverage, `globallyComplete: false`, and a continuation.
Only edges with both endpoints in the page are returned. Neighbour pagination
retains the root in every page so all its visible incident relations can be read.
Edges crossing ordinary inventory pages require a neighbour query. No hidden
totals are exposed.

Missing and invisible roots return identical empty slices. Invalid/stale cursors
return `system_graph_cursor_invalid` (400); changing observations return
`system_graph_changed` (409); authorized limits return
`system_graph_limit_exceeded` (422); contributor failures return
`system_graph_unavailable` (503). Raw exception details are never returned.
Cancellation uses the request token.

## Initial coverage

| Owner | Evidence |
| --- | --- |
| Resources / Identity | Exact identities, source origin kind, persisted scope hierarchy and current authorization. |
| Models / Runtime Profiles | Current exact ModelProfile and RuntimeProfile metadata. |
| Agents | Definitions, owner-relative profile declarations, immutable revisions, recorded profile generations and revision hashes. |
| Flows | Definitions, immutable versions and recorded Agent revision UIDs in direct, routing, workflow and orchestration definitions and top-level Agent/router graph steps. |

Inspection reads recorded publication pins; it never invokes the publication-time
Agent resolver. Nested composition, Tools, ToolSets, Queries, Knowledge, Storage,
complete reverse paths and impact belong to #1197. Missing pins and unsupported
relations do not prove no dependency exists. No content or provider is probed.

## Multiple versions

A resource UID has a stable definition node. Each publication has a separate
identity: native revision UID for Agents, owning Flow UID plus exact version for
Flows. Recorded generations differ from current definitions and ETags. Publishing
a newer version never changes the older version's dependency edges.

A generation pin is not a retained executable profile. Deletion/recreation yields
a new UID; historical pins never rebind by name. Purged history cannot be rebuilt
from an index. Adoption policies remain owned by #1031 and #1161.

## Permissions and consistency

The application composes current RBAC and Discover/Use. Discover-only permits
catalogue identity but no bindings or relations. Use-only creates no catalogue
visibility. Revision visibility follows its exact owning Agent. Hidden endpoints
never enter public paths, counts, diagnostics or cursors.

Each request rebuilds twice and rejects differing authorized observations. This
is not a shared transaction or globally complete snapshot. Hashes exclude hidden
resources and observation time. Cursors expire after five minutes and after
process restart. Reconstruction is limited to 10,000 authorized nodes and 20,000
relations. SQLite is sufficient; there is no persistent graph database or cache.

The older DAT's operational-Workspace placement narrative and the exact invocation
contracts remain an explicit discrepancy, described in the ADR. This API changes
neither execution placement nor accepted decisions. Ownership, authorization,
invocation and Worker placement remain separate.

Flow version enumeration uses native bounded pages in both SQLite and PostgreSQL.
Each authorized observation collects nodes first, then scans relations and retains
only those with authorized endpoints before applying the relation budget. Hidden
relations cannot exhaust that public budget. The second observation repeats both
phases; no raw cross-family edge index is retained.
