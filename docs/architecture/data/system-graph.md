# Semantic SystemGraph

SystemGraph is the read-only cross-family metadata projection introduced by
[#1196](https://github.com/gbaudrit/agentstration/issues/1196).
[ADR-0239](../../decisions/0239-system-graph-is-an-authorized-rebuildable-semantic-projection.md)
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

## Family coverage

| Owner | Evidence |
| --- | --- |
| Resources / Identity | Exact identities, source origin kind, persisted scope hierarchy and current authorization. |
| Models / Runtime Profiles | Current exact ModelProfile and RuntimeProfile metadata. |
| Agents | Definitions, owner-relative profile/Tool declarations, exact declared ToolSet selections, immutable revisions, recorded profile generations and revision hashes. |
| Flows | Definitions, immutable versions and recorded Agent revision UIDs in direct, routing, workflow and orchestration definitions and top-level Agent/router graph steps. |
| Tools | Provider metadata, relay generations, ToolDefinition publications and ToolSet member pins. |
| Schemas / Functions | Schema publications and family-owned function/type versions and digests. |
| Data Sources | Profile Tool/provider pins, dataset operation/Storage pins, Query publications and contract digests. |
| Connected Services / Storage | Type contracts, operation/service generations, value references, provider contributions and collection Schema pins. |
| Knowledge | Generated access-relay source-identity bindings, source input pins, Snapshot history and recorded input/output Storage and binding identities. |
| Triggers | Recorded Flow UID/version targets. |

Inspection reads recorded publication pins; it never invokes the publication-time
Agent resolver. Flow-call/composition/function/schema references require recorded
UID evidence. Direct Flow Tool references use the existing owner-relative lookup
and remain explicitly dynamic. Missing pins and unsupported relations do not prove
no dependency exists. No content or provider is probed.

`GET /api/system-graph/coverage` reports static supported and unavailable evidence.
Historical Flow bindings without UIDs, opaque settings and native Entry identity
integration remain unavailable. #1189 proposal refresh is not invented. Coverage
limitations are not empty dependency findings.

## Reverse impact and change comparison

`GET /api/system-graph/impact?root=<graph-id>` explains authorized reverse paths.
Optional `namespace`, `maximumDepth` (1–32, default 16) and `maximumPaths` (1–1,000,
default 200) bound the request. Paths retain source fields and selection reasons;
cycles and truncation are explicit. Traversal follows executable dependencies,
generation links and publication membership. Declared, logical, operational and
Parameter/Secret value-reference edges do not enter publication closure.

`GET /api/system-graph/changes?from=<graph-id>&to=<graph-id>` compares selected
generations/publications and their paths within one authorized observation. It
distinguishes identity replacement, generation drift, compatible/changed Query
contracts and unknown publication compatibility. It accepts the same bounds and
namespace filter. Missing and invisible selections are indistinguishable. These
read-only APIs are not reconciliation plans or permission grants.

Query Tool extraction shares invocation publication selection: exact stays exact;
latest means publication time, including a lower semantic version published later.
Compatible filter/order changes need no Tool/Flow republication. Incompatible
latest fails contract comparison without older fallback. Query bodies and literal
values are omitted. [ADR-0240](../../decisions/0240-system-graph-impact-uses-authorized-family-evidence.md)
records these rules.

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

Derived pin/reference nodes require binding inspection rights on the consumer that
discloses them as well as the target. A hidden consumer cannot change public pin
counts, snapshot digests or paths of an otherwise visible dependency.

## Authored systems and Console exploration

The `Systems` family owns persisted logical `SystemResource` definitions outside
the projection. A name/description and up to 128 exact resource memberships use the
existing scoped resource store and ETags. The API `/api/systems` provides filtered
list/read, create, rename, delete, add-member and remove-member commands. Membership
requires Discover; mutations require the existing write/delete permissions. Hidden
members are filtered without private totals. Partial membership edits fail uniformly;
rename preserves unseen members. Delete removes the System, never member resources.
Deleted/recreated UIDs are not rebound. Its contributor emits `system-membership`
edges with `definition.members` provenance; those edges are logical, outside impact
closure. Discover-only still exposes no executable bindings.

`/system-explorer` uses URL `context`, `namespace`, System-owner `scopeRef` and exact
semantic `root`. It loads only after the first interactive render, through the
separate finite `GET /api/system-graph/stream` NDJSON endpoint. An authorized start
frame precedes family enumeration, the first node contribution arrives immediately,
and later batches carry up to 20 nodes or 100 relations. Five-second context-checked
heartbeats and a client 30-second idle deadline replace full-body buffering for
this route. The strict inventory/cursor, impact and reconciliation APIs keep their
existing consistency checks.

With `root`, the stream follows outgoing authorized dependencies up to
`maximumDepth` (1–32, default 16), alongside direct consumers of the selected root.
It does not expand other consumers of shared dependencies. The namespace filter
selects the root; exact dependency paths can cross namespaces when authorized.
Without a root, namespace continues to filter every inventory node. Missing/hidden
roots are indistinguishable; depth truncation is explicit. The strict inventory
API retains its direct-neighbour contract. Out-of-order edges and cycles are handled
within the bounded authorized working set.

Knowledge access relays expose a dynamic link to their fixed KnowledgeSource UID
only when their family-owned annotation and exact `/source` binding agree with the
current source identity. Deletion/recreation never rebinds by name. Literal values
are not exported. Agent drafts expose Tool and selected ToolSet declarations;
revision pins keep their separate historical evidence.

Stream observations are provisional, bounded to 10,000 authorized nodes / 20,000
relations, and never globally complete. Data batches recheck current context,
affected identities, binding owners and both endpoints; completion checks emitted
identities again. Visible metadata change, detected revocation, sequence gaps, transport
failure or incomplete EOF invalidate and clear the whole client observation.
Truncation is explicit. A stream observation identifier is not a strict snapshot
digest or reconciliation read set. There is no persistent index or resume cursor.

Received resources remain visible during loading. Table and diagram use local
80-node windows, preserving the root for neighbour views; next/previous windows
reuse received data. JSON exports only the authorized resources actually received.
Tool/ToolProvider labels use stored display names. Recorded pins may reuse the
current authorized definition label by UID for presentation, retaining exact graph
identity and evidence; paired technical names remain in the inspector/export.
Family detail/Flow Designer links retain current context and exact owner. Logical
System listing loads independently. The neutral table, inspector and export work
with `renderer=list` or the optional diagram disabled.

`Web.SystemExplorer` alone translates neutral identities into LikeC4 Model Builder,
browser WASM layout and React. It retains the current graph during layout and
coalesces pending batches, with one active layout and one latest pending request.
Unchanged windows and heartbeat frames do not relayout. Disposal prevents a late
old-context layout from committing. The adapter remains capped at 100 nodes and
20 seconds. Local assets/shadow-root styles isolate the renderer; no graphical
field or persisted layout enters the semantic model. Node is a build dependency.
[ADR-0241](../../decisions/0241-authored-systems-and-replaceable-explorer-rendering.md)
and [ADR-0244](../../decisions/0244-system-explorer-streams-provisional-authorized-observations.md)
record authority, progressive delivery and limits. New reusable UX scenario #1201
remains deferred until explicit UX validation and merged owning UX PRs; existing
regressions and implementation smoke remain required now.

## Selected reconciliation review

`Reconciliation` consumes authorized neutral impact/inventory ports. POST
`/api/system-reconciliation/preview` reviews an exact source pair and up to 32
explicit consumers/target version proposals. `/revalidate` recomputes current
authorized relevant evidence against the reviewed fingerprint. No source resource,
publication, value, grant or runtime state is mutated. The result is always partial
and non-executable; compatible dynamic Query changes are no-op, incompatible
contracts block, Agent adoption awaits #1031's approved primitive, and other
publication requires reviewed conditional family commands.

`/system-explorer/reconciliation` presents comparison, explicit consumer selection,
ordered review and revalidation. Its fingerprint covers relevant identity, pin,
current authoring ETag, relation and choice evidence; unrelated changes do not by
themselves stale the review. During construction, a final impact observation and
context check detect concurrent changes. Neighbor-slice hashes are not compared to
whole-observation hashes. See [ADR-0242](../../decisions/0242-reconciliation-preview-reviews-selected-authorized-evidence.md).
Durable selective execution and receipts remain an explicit remaining part of #1199;
no generic ChangeSet or renderer command substitutes for them. Deferred scenario
#1202 retains its maintainer-validation and merge gates.

## Separate operational evidence

GET /api/system-operations and private /hubs/system-operations streams compose
authorized volatile facts for one exact graph node. The opt-in Explorer panel and
ephemeral diagram notes consume the same separate facts. Semantic graph/export
data is unchanged. Disconnect or revocation clears facts; epoch/sequence guards
require a fresh snapshot after gaps or reconnecting (toggle the overlay off/on).

Scope, actor and RBAC are server-derived and rechecked before delivery. Native
identity, owner, namespace, invocation and version filters precede sample limits.
Direct Agent/AgentRevision Runtime runs and newly attested Flow publications are
supported. Older Flow publications remain unknown for Flow-identity metrics. Recorded Agent
bindings and native assignment coordinates are shown separately, without becoming
direct Agent metrics. WorkItem backlinks require exact current authorized execution proof.
Drill-down opens existing scoped Runtime/Flow details.

Samples contain active runs and terminal runs updated within the selected window,
observed counts, mean completed-run duration, direct reported tokens and explicit
coverage. Missing evidence is null, never zero/healthy. Worker presence requires
current Platform administrator authority and reveals no global load/count.
Attempt, lease expiry, fencing and presence remain distinct.

Console defaults are 15 minutes / 50 runs; server bounds are 1–1440 minutes /
100 runs, eight attempts per run and 64 private streams globally / eight per actor /
one per connection. One-frame backpressure avoids queued ticks. Optional OTel
dimensions are bounded family/coverage, never resource/run/worker IDs.

Runtime/Flow can retain physical Workspace and explicit logical Instance/Tenant
coordinates separately. Both are displayed; definition owner remains distinct.
The accepted ADR-0180 exact-scope drift is recorded without inferring new scope
or rewriting history. See [ADR-0243](../../decisions/0243-system-operations-is-a-separate-authorized-volatile-overlay.md).
Scenario #1203 retains its explicit UX validation and merge gates.


### Recorded Flow bindings and attested invocations

The operational facts have a separate FlowBindings collection for Agent roots.
A consumer Flow can be in another namespace; its stored binding must still match
the exact selected Agent owner/namespace/UID and requested revision/hash/generation.
Logical/physical invocation scope, tenant and actor filters precede the SQL sample
limit. This association is not an assertion that the Agent executed.

Invocation groups require distinct native turn IDs for the same run and participant,
one unambiguous recorded identity, a known attempt/session/fencing proof and, when
present, an exact StepExecution run/version/execution-hash coordinate. Child runs
retain their own run coordinate within the root assignment. Groups from sibling
runs or participants are excluded before counting. Missing legacy hashes and
ambiguous bindings produce unknown evidence, never a name-based inference.

These facts use durable assignment metadata without invoking an execution-material
resolver. Consumer Flow state, observed turns and direct-run metrics are separate.
At most 100 binding associations and eight groups per binding are shown; flags
report truncation. The panel offers scoped Flow/step drill-down in both locales.
Neither these associations nor their live updates enter semantic graph JSON or
publication/impact closure. See ADR-0243.

### Definition photos and Agent deployment views

System Explorer separates draft/retained-revision definition photos (including two
explicitly selected revisions) from AgentDeployment configuration photos. The
Agents-owned assignment endpoint records an exact revision, environment and logical
target without provisioning or starting a Run. Reassignment uses ETags and retains
up to 64 immutable configuration values atomically with the current assignment.
Read access to each configuration requires access to its exact retained revision;
purged or hidden revisions are unavailable. Legacy deployments gain no inferred
configuration.

Native LikeC4 deployment views contain environment/target nodes and runtime
container instances; the Agent revision remains a logical component inside the
runtime. Desired targets are declarations. Observed topology instead uses only
explicitly associated Runs and attested Worker/session attempts, subject to the
existing Worker-details permission. Runs without deployment/configuration UIDs
remain unattributed. Observation time, bounded sample coverage and unknown physical
infrastructure/pool membership are visible. Saving an assignment does not route
work, change adoption policy or establish placement. See
[ADR-0245](../../decisions/0245-agent-deployments-record-exact-durable-assignments.md).

An Agent's published `AgentRevision` records are listed in the **Definition**
perspective. Publishing/preparing a revision does not create an environment/target
assignment. The **C4 deployments** selector lists authorized assignments for the
exact Agent UID across deployment namespaces; inventory without an Agent keeps its
namespace filter. The selected configuration is queried and reassigned at the
deployment's own namespace and owner, independently of the Agent namespace.

An empty deployment perspective does not display the Agent dependency graph as a
fallback. It explains the missing assignment and links to the published revisions.
Instance/Tenant readers can navigate to their authorized Workspace contexts while
preserving the exact Agent selection. Recording remains subject to Workspace
ownership, resource-write permission and Discover/Use of the referenced revision;
navigation never creates or adopts an assignment automatically.

### Tool access summary

The default diagram groups a resource's current and recorded generations by UID.
ToolSets appear as versioned access paths on Agent → Tool links; ToolDefinition
publications move to link provenance. Several visible ToolSets can aggregate on a
selectable link. The technical-details control restores the exact window; list
and JSON export always retain the received semantic observation.

The Agents contributor supplies selected-member evidence through `toolset-access`.
Its `ViaNodeId` is an additional authorized endpoint: strict and progressive reads
require Discover and binding inspection of it, and streams emit and recheck it
before delivering the relation. Selection requires exact retained publication
identity and matching recorded Tool pins, never all members inferred from topology.
Native publication UIDs map to canonical ToolSet publication IDs without rewriting
Agent revisions. Unknown legacy origins remain unknown.

The summary contracts typed ToolDefinition paths with their original steps retained
for inspection. It changes no executable closure or resource. A ToolSet is an access
grouping; C4 container boundaries require actual execution units. See
[ADR-0246](../../decisions/0246-tool-access-summaries-preserve-authorized-provenance.md).

### Resource-centered component

`ResourceRelations` prepares later resource-screen adoption using the same LikeC4
adapter and authorized stream, with outgoing dependencies or incoming usages.
Generation grouping, selected ToolSet provenance and exact publication identities
are preserved. Visual depth is applied after summary contraction within an
80-node window. Actor context stays independent of resource ownership while
recentering across namespaces. Local icons, root emphasis, theme and viewport
remain renderer concerns. No resource screen is automatically changed. See the
[integration contract](../../reference/resource-relations.md).
