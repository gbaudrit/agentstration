# Governed structured documents

The active [#1150](https://github.com/gbaudrit/agentstration/issues/1150) vertical
adds JSON documents and queries to ASP without changing the existing content
profile. PostgreSQL JSONB is an optional autonomous provider. Memory integration
[#1156](https://github.com/gbaudrit/agentstration/issues/1156) remains on hold.

## Contract and responsibilities

| Boundary | Responsibility |
|---|---|
| ASP Abstractions / Server / Client | `asp.documents/1`, canonical MCP schemas, exact logical selection, conditional document operations, query profile pins, protected cursors and optional immutable publication |
| ASP OData | Optional `odata/4.01@1.0` grammar/model binding through Microsoft.OData.Core; no generic public parser AST |
| AEP | Authenticated contribution discovery, typed capabilities and the existing protected `/aep/storage/bindings` exchange |
| Connected Service | Typed host, port, database, username and secured password exports; existing Parameter and Secret resolution |
| Storage | Exact implementation/service/export pins, common operation envelope and logical collection declarations |
| Infrastructure document gateway | Current scope, Use and permission checks; intersection and operation-specific narrowing; late AEP binding; audit and tracing |
| ToolDefinitions / relays | Explicit immutable Tool publication; fixed Storage revision, collection, policy and profile |
| Knowledge | Acquisition/projection composition, immutable publication, canonical Artifact materializations, exact Snapshot selection and retention leases |

No agent argument chooses a physical SQL schema, table, connection string or
credentials. A Storage Type binds compatible service exports explicitly; it does
not require that the Connected Service Type came from the same extension.

```mermaid
sequenceDiagram
    participant Caller as Agent or Flow
    participant Tool as Governed relay
    participant Product as Document gateway
    participant Service as Connected Service / Secrets
    participant Extension as AEP / ASP provider
    participant Database as Optional PostgreSQL
    Caller->>Tool: Public operation arguments
    Tool->>Product: Fixed exact Storage, collection, profile
    Product->>Product: Reauthorize current scope, Use, operation and limits
    Product->>Service: Resolve explicit exports and short-lived Secret capability
    Product->>Extension: Protected binding on existing AEP channel
    Product->>Extension: Canonical ASP MCP operation
    Extension->>Extension: Enforce fixed selection, policy and query limits
    Extension->>Database: Partitioned parameterized SQL
    Database-->>Caller: Bounded document/page through governed boundaries
```

## Logical collections

Collections are bounded Storage configuration, rather than another resource or a
Data Source mode. A Data Source still owns governed acquisition. Each collection
declares `uid`, `name`, `displayName`, `enabled`, `schema` and `policy`.
The server assigns its UID. Removing and recreating the same name creates a new
identity and never reattaches old documents. Collection changes participate in the
Storage binding digest and require explicit reconciliation. Existing dependencies
continue to protect exact historical references.

The policy separates `get`, `put`, `delete`, `query`, `publish`, allowed profile
IDs and page/document/result limits. It can only narrow the Storage envelope.
Execution reuses `runs/execute` and the existing `resources/read`,
`resources/write` or `resources/delete` permission, plus scoped Resource Use.
No second permission registry is introduced.

## Query profiles

A request pins `{id, version, encoding, modelDigest}` from discovery. IDs and
encodings are open strings. An unknown version, encoding or model fails rather
than falling back. Other profiles can carry text encoded as JSON or structured
objects; the generic client, server and Tool query payload are not OData AST types.

The initial profile uses standard OData expressions in a JSON options object:

```json
{
  "$filter": "status eq 'degraded' and metrics/errorRate gt 0.1",
  "$orderby": "timestamp desc",
  "$select": "status,metrics/errorRate,timestamp"
}
```

The outer ASP request supplies `pageSize` and an optional opaque `cursor`. The
profile does not expose `$top`, `$skip`, SQL, joins, `$expand`, arbitrary functions
nested lambda captures or nested transformation pipelines. Supported features include comparisons,
`and`/`or`/`not`, bounded `in`, `contains`/`startswith`/`endswith`, `any`/`all`,
explicit selection, sort, and one simple aggregation:

```json
{
  "$apply": "groupby((service),aggregate($count as Count,metrics/errorRate with sum as Total))"
}
```

This is a bounded OData query profile over MCP, not a complete OData HTTP server.
Discovery states the subset. Sorting and grouping require declared scalar fields;
sum requires a declared number. An optional document JSON Schema builds the model.
The bounded supported schema keywords are `type`, `properties`, `required`,
boolean `additionalProperties`, `items`, `minimum`, `maximum`, `minLength`,
`maxLength`, `minItems`, `maxItems`, `enum`, local `$defs`/`$ref`, nullable `anyOf`,
asserted `date-time`/`uri` formats and annotations. One type plus optional `null`
is supported, including `anyOf` with one typed branch and one unconstrained null
branch. Reference siblings are limited to annotations. References must be local
JSON Pointers; remote references, cycles and unsupported unions fail explicitly.
Schemas are bounded to 64 KiB, eight expansion levels and 2,048 expanded nodes.
Validation uses JsonSchema.Net without remote resolution. The original schema
remains stored and hashed; expansion is only an internal validation/query model.
This ASP profile does not change the separate Schema-family contracts attached
to acquired Artifacts. Shared Schema discovery/adoption across OpenAPI, Artifacts
and collections is tracked separately in [#1172](https://github.com/gbaudrit/agentstration/issues/1172).
Invalid collection definitions return
`storage_collection_schema_invalid` with bounded `validationIssues` paths/codes.

Missing values compare as null. `eq null` matches missing and explicit null;
`ge`/`le` are true for two nulls, and other ordered null comparisons are false.
Non-null comparisons use JSON types without string-to-number coercion.
Numbers must be exactly representable as .NET decimal; strings use Unicode
code-point order. Sort adds document ID as a final ascending tie breaker, with
null first ascending and last descending. Mutable keyset pages may observe
concurrent writes; immutable Snapshot pages retain their sealed selection.

Limits are 64 expression nodes, depth 12, four sort keys, 32 in-list values,
16 selected paths, four grouping keys and eight aggregates. Documents are at most
64 KiB, results at most 192 KiB and pages at most 100 items, all further narrowed
by policy. Aggregate results fit one bounded page or fail explicitly. Request
payloads, parsing, SQL execution and cancellation remain bounded. Cursor integrity
binds caller/grant, StoreRef, collection/publication, exact profile/model, query
and page size. ASP cursors expire after five minutes; Knowledge continuations use
a shorter four-minute bound and an existing retention lease.

Get accepts an optional exact current revision. Put without `expectedRevision`
creates only; replacement and deletion require the current revision. Retrying an
uncertain mutation must preserve request ID, deadline and arguments. There is no
automatic mutation retry. Structured errors include conflict, unsupported profile,
invalid query/reference, schema validation, limit, timeout/cancellation and
unavailable exact version. Errors and traces do not log document bodies or Secrets.

## Optional PostgreSQL extension

Publish or run `src/Agentstration.Extensions.PostgreSql` independently, enroll it
through the existing AEP workflow and explicitly create its Connected Service and
Storage. The contributed `postgresql.jsonb@1.0.0` requirements are typed `host`,
`port`, `database`, `username` and secured `password`. No automatic connection or
Storage is created. Configuration is `{}`; the envelope contains document policy
fields. Enable desired write/delete/publication rights explicitly.

`PostgreSqlDocuments:AllowedClients` or explicitly allowed paired enrollment
governs transport callers. `AllowPairedClient` requires `Aep:EnrollmentMode` to be
`PairingCode`. Existing shared-key enrollment is also supported. Scope assignment verifies the
manifest using the existing registration Secret and passes that authenticated
manifest to the shared contribution provisioners. TLS defaults to
VerifyFull; `AllowLoopbackWithoutTls` applies only to an explicitly configured
loopback host. `ProvisionSchema=true` is an operator-owned setup option. Production
roles can instead receive the provisioned tables and DML rights separately from
the provisioning role.

The extension owns the `asp_jsonb` layout. Compound keys independently constrain
Store, binding, collection, publication and document. A path-ops GIN index supports
the extra containment predicate used for declared scalar equality. Typed range
and sort expressions can use bounded scans; SQL timeouts and collection limits
remain enforced. Operator tuning must be benchmarked against the actual workload.
Mutable collections admit at most 10,000 documents, retained publications at most
256 per collection, each with at most 100 documents. Retire old publications using
the governed producer lifecycle. Mutation receipts are transactionally persisted
and bounded; cleanup is incremental. Pools use at most four connections per exact
connection configuration, and SQL commands time out after ten seconds.

Aspire startup is opt-in with `PostgreSqlDocuments:Enabled=true`. It starts the
extension and reuses development AEP enrollment; it does not provision a database
or invent service bindings. The default product still runs without PostgreSQL.
The container distribution is `deploy/postgresql-documents/Dockerfile`.

Relational access to existing databases, governed SQL, JSONPath, full-text search,
pgvector, embeddings and hybrid Search are future profiles/capabilities. None is
required or exposed by this delivery.

## API, Tools and Console

Under `/api/storages/{name}/collections/{uid}`, with explicit `scopeRef` and
optional `namespace`, use `GET capabilities`, `GET documents/{id}`,
`POST documents`, `POST delete`, `POST query` and `POST tools/publish`.
Tool publication chooses explicit operations, a version and, for query, an exact
discovered profile. Metadata discovery intersects the authenticated contribution
and current policies without resolving Secrets or probing the physical database. Published Tools are assigned explicitly to agents or Flows;
they are not automatically granted. Draft and published document relays appear
as Storage dependencies and prevent destructive deactivation or rebinding. Query/write/delete rights are independently
restricted. Relay-fixed arguments cannot be replaced by caller JSON or the LLM.
Document Tools and the structured Knowledge query Tool explicitly declare an
object output contract. Published Flows pin that contract through the existing
Tool contract resolver; query-language and document-model pins remain independent.
Republish structured relays from earlier integration builds before using them
directly from a Flow, then republish consuming Flows to pin the new contract.

The Storage editor declares collections and their rights/schema. Storage details
inspect effective capabilities, verify operations and explicitly publish a Tool.
All UI labels are available in en-US and fr-FR. An expired or uncertain mutation
retains its original identity for an explicit retry; changing the draft creates
a new request. Documents entered in the verification screen are persisted.

Document-only Storages require an explicit collection in both source editors.
The forms explain missing read/query/publication rights or an empty collection
catalog and link to the Storage definition before allowing submission.

## Structured acquisitions and document granularity

A Data Source dataset can explicitly pin `documentCollectionUid` alongside its
exact Storage revision. An acquired JSON object is canonicalized, stored in its
own immutable publication and registered as an Artifact with the existing
acquisition contract/provenance. Publication identity includes the source and
acquisition UIDs. Exact reads and retention retirement reuse that publication;
later acquisitions cannot replace historical results. This path requires an
enabled collection permitting read/query/publication, with current grants and
the existing dataset read/write intent. Content-backed acquisitions retain their
existing behavior. Non-object results need explicit normalization and are refused.

For an OpenAPI acquisition returning `{ "total_count": 88, "results": [...] }`,
the complete response is one document. Its collection schema can describe nested
objects, arrays and local definitions. `results/any(a:a/status eq 'EN COURS')`
selects complete response documents containing a matching alert; it does not
flatten, trim or count individual array entries. JSON strings retain the profile's
string comparison semantics even when their date-time format is validated.

Publishing one document per array element requires an explicit normalization
processor producing a multi-Artifact manifest. The current standard Flow catalog
does not yet provide a turnkey deterministic operation for that split. Generic
mapping, Artifact processing and array batches alone do not provide it. The
structured acquisition/schema corrections do not add this separate operation or
infer completeness of the OpenAPI acquisition's Artifact contract. The builtin
HTTP acquisition has a different output envelope (`mediaType` and textual
`content`); consumers must use the actual operation's output shape.

## Structured Knowledge

Select `documentCollectionUid` in the Knowledge output Storage. A projection
prepares one immutable collection publication, seals it, registers canonical JSON
Artifact materializations and then publishes the Snapshot. Hashes refer to
canonical JSON bytes, not the original lexical JSON. Input acquisition and Flow
provenance remain recorded.

Native `read`, `search_regex` and `get_status` remain available where supported.
The new `query_documents` Tool is published only when the actual output Storage,
collection policy and profile permit it; it fixes source identity and profile.
There is no consumer Knowledge put/delete capability. Exact historical queries
use the Storage, publication and model in the Snapshot evidence. A new active
publication never redirects an old Snapshot or cursor.

Use `POST /api/knowledgesources/{name}/content/query-documents` for native query
and `GET .../content/document-capabilities` for metadata discovery. Initial query
can prepare current data using the existing freshness/coalescing mechanism.
Continuation supplies only the protected cursor. Historical selection supplies
`snapshotId` and its matching profile. Existing read/query leases protect retained
content from retirement, subject to current authorization on every request.

## Deterministic validation

The provider test project runs the same MCP cases with a bounded memory fixture
and, when `AGENTSTRATION_TEST_ASP_POSTGRES` is set, a dedicated local PostgreSQL.
Product tests exercise real AEP contributions, late-bound Secrets, MCP, separate
agent relays, acquired JSON normalization and historical Snapshot queries. They
require neither an LLM nor internet access at execution time.

```bash
dotnet test --project tests/Agentstration.Extensions.PostgreSql.Tests -c Release
dotnet test --project tests/Agentstration.Api.Resources.Tests -c Release \
  --filter 'FullyQualifiedName~TwoAgentRelaysUseLateBoundExports|FullyQualifiedName~AcquiredJsonProjectsToImmutableStructuredKnowledge'
```

The PostgreSQL CI workflow supplies a disposable loopback database and runs the
real provider rows. Without the variable, optional PostgreSQL rows are explicitly
inconclusive; the deterministic memory path still executes. The separate reusable
UX journey [#1158](https://github.com/gbaudrit/agentstration/issues/1158) remains
blocked until maintainer UX approval and merge, as required by `AGENTS.md`.

See ADRs [0220](../decisions/0220-asp-document-capabilities-and-open-query-profiles.md),
[0221](../decisions/0221-bounded-standard-odata-query-profile.md),
[0222](../decisions/0222-logical-document-collections-remain-storage-configuration.md),
[0223](../decisions/0223-postgresql-jsonb-is-an-autonomous-asp-provider.md),
[0224](../decisions/0224-structured-snapshots-pin-immutable-document-publications.md)
and [0225](../decisions/0225-structured-storage-does-not-define-memory-policy.md).
