# Storages API

Storages are governed logical resources. A Storage selects an exact StorageType
version, its own fixed envelope and, for remote types, an exact Connected Service
with manually confirmed export mappings. It owns no credentials. Data Source and
Knowledge rules cannot replace those selections.

## Types and discovery

`StorageType` is an Instance-, Tenant- or Workspace-owned resource with the normal
UID, namespace, generation, governance and access policy. Core, operators and
authenticated extension adapters can supply types. An extension may synchronize
only its own resources, with exact registration UID and contribution evidence.
There is no link to the extension that supplied a Connected Service type.

Each immutable version declares:

- configuration and envelope JSON schemas and their SHA-256 digests;
- whether a Connected Service is required, plus typed/protected requirements;
- implemented list/stat/read/write/delete and optional stable-version/retention
  capabilities, and bounded page/read/write limits;
- optional exact extension contribution evidence and deprecation metadata.

The complete version exposes its computed `contractDigest` in the catalog. Consumers pin that
digest as `contractDigest`, in addition to `typeUid`, `version`, `schemaDigest` and
the exact type `ResourceReference`. New versions are additive; published versions
cannot change or disappear. Preferred-version updates never rebind Storages.

The builtin Core-owned `local@1.0.0` requires no Connected Service. Configuration
is `{}`; its envelope declares bounded content permissions and restrictions.
It supports list/stat/read/write without stable historical versions or Snapshot
retention. [Storage execution](storage-execution.md) describes its actual limits.
#1040 supplies the Artifact adapter and guarded default provisioning.
Existing Artifact Flows, acquisition Tools, durable reads and histories continue
through their existing implementation.

Catalog results include per-version availability. The default metadata adapter
checks the exact extension registration UID, generation and enabled state locally. A
changed provider registration cannot silently remap an immutable type version. #1040 may
supply provider observations through `IStorageTypeAvailabilityProvider`; catalog
and management calls never perform a storage operation or fetch credentials.

## HTTP operations

All routes require the normal resource read/write/delete permission. Detail and
mutation routes select the canonical resource name plus exact `scopeRef` and
optional `namespace` (default `default`). There is no second Storage identifier.

| Method | Route | Behavior |
|---|---|---|
| GET | `/api/storage-types` | Visible type catalog |
| GET | `/api/storage-types/{name}` | Exact governed type and version availability |
| POST | `/api/storage-types` | Create a type |
| PUT | `/api/storage-types/{name}` | Update metadata or append immutable versions |
| POST | `/api/storage-types/{name}/versions` | Explicitly append a new immutable version, optionally making it preferred |
| GET | `/api/storage-types/{name}/usages` | Up to 100 discoverable Storage references, preserving exact scope/namespace |
| DELETE | `/api/storage-types/{name}` | Delete an unused user-managed type |
| GET | `/api/storages` | Scoped visible Storage collection |
| GET | `/api/storages/{name}` | Exact resource, current diagnostics and ETag |
| POST | `/api/storages` | Create with complete explicit bindings |
| PUT | `/api/storages/{name}` | Update display metadata, enablement or use policy |
| GET | `/api/storages/{name}/usages` | Consumer-reported dependencies |
| POST | `/api/storages/{name}/reconcile` | Observe drift or explicitly accept a complete new definition |
| POST | `/api/storages/{name}/deactivate` | Disable safely while retaining the resource |

Collection routes accept `scopeRef` (defaults to the authenticated scope), `skip`
(0–1000000) and `take` (1–100, default 50). They return `{ value, nextLink }`.
Foreign scopes and undiscoverable resources remain inaccessible. Reads recompute
availability without mutating saved choices or changing ETags.

Creation uses `{ name, properties, scopeRef, namespace }`. A local definition can
be constructed from the exact catalog resource:

```csharp
var version = type.Definition.Versions.Single(v => v.Version == "1.0.0");
var properties = new StorageProperties
{
    DisplayName = "Local content",
    Type = new()
    {
        Type = new(type.Name, type.ScopeRef, type.Namespace),
        TypeUid = type.Uid,
        Version = version.Version,
        SchemaDigest = version.SchemaDigest,
        ContractDigest = version.ContractDigest
    },
    Configuration = JsonSerializer.SerializeToElement(new { }),
    Envelope = JsonSerializer.SerializeToElement(new { })
};
```

Remote definitions additionally save `connectedService` (exact address, scope,
UID and generation), `connectedServiceType` (exact type reference/UID/version/
schema digest), and `bindings`. Each binding has a `requirementId` and the complete
selected export descriptor as `export`. The caller explicitly supplies these
entries. Matching names, owners, formats or suggestions never generate a binding.
One service may back multiple independent Storages.

Requirement member identifiers use the Connected Service member convention: 1–64
ASCII letters, digits, `.`, `_` or `-`, starting with a lowercase letter. They are
preserved exactly, including provider ids such as `access_key_id`.

Every mandatory requirement must be bound. Export ids must belong to the selected
exact service type and still exist with the saved descriptor. Scalar type,
standard/secured protection and any required format must agree. Disabled,
recreated, inaccessible or stale services and missing/unbound exports fail.
Inspection validates binding references without resolving Secret values.

The configuration and envelope retain provider-owned key casing and JSON values.
Both are schema validated. Administrators authorized to inspect a resource can
read its saved configuration, but it contains no copied Secret values. Do not put
credentials in non-sensitive configuration; express them as secured requirements
bound to Connected Service exports.

## Concurrency, drift and diagnostics

Every update, type deletion, reconciliation and deactivation requires the exact
`If-Match` ETag. Missing headers return 428; conflicting versions return 409.
Bindings, type, connection, configuration and envelope changes through PUT return
`storage_reconciliation_required`. POST reconciliation with `{}` only observes
drift and retains desired configuration and generation. To accept a new selection,
send `{ properties: /* complete explicitly confirmed definition */ }` with the
current ETag. Successful acceptance advances the generation. It never infers an
export mapping or switches a recreated service to its new UID.

Consumer dependencies block deactivation and binding changes with
`storage_in_use`. The `IStorageUsageProvider` port allows #1041/#1042 to report
their live and historical dependencies. A disabled or missing provider leaves the
Storage, its exact contribution evidence and saved bindings intact. There is no
Storage DELETE route that could erase those references.

Problem Details contain an invariant `code`, status and bounded diagnostic text.
Typical codes include `storage_mapping_incomplete`, `storage_export_incompatible`,
`storage_export_stale`, `storage_service_stale`, `storage_type_stale`,
`storage_provider_unavailable`, `storage_access_denied` and `storage_in_use`.
Diagnostics never echo configuration, export values or credentials.

The Schemas family's builtin JSON Schema profile is used: maximum 256 KiB per
document, depth 16, no external references, bounded regular expressions and no
silently ignored unsupported keywords. Types contain at most 32 versions and 64
requirements; a Storage contains at most 64 bindings. Opaque configuration is not
a universal bucket/path/table contract.

## Consumer and adapter ports

`IStorageResolver.ResolveAsync` consumes a `StorageRevisionReference` containing
the canonical scoped address, UID, generation and `bindingDigest` from the
resource's observed state. The consumer context must match the authenticated
scope. Storage and StorageType use is authorized by the existing generic
`ResourceAccessPolicy.Use` decision, with exact identity and same/descendant scope
checks. A Tenant grant with propagation authorizes its Workspaces; Discover alone
does not authorize use. The embedded Storage-family UsePolicy metadata is not an
additional authorization gate while that vertical is deferred to #1078. No grants
are generated automatically. Resolution also revalidates type, schemas, service,
manual mappings and availability. Connected Service inspection keeps the independent
checks owned by that family. Old/forged revisions and revoked grants fail on each
call.

`ProtectedStorageContext` exposes a serializable logical revision and exact type/
service evidence. Configuration, envelope and bindings remain separate protected
properties, excluded from JSON and redacted from its string representation. There
are no secrets in this object. It is a selector/evidence object, not a bearer
credential; the resolver must be called under current authorization before use.

#1040 consumes these ports, resolves Secret references late through Connected
Services, and maps the separate authority layers into protected ASP invocation
contexts. It owns actual caller authorization at the provider, AEP contributions,
ASP capabilities and Artifact integration. It must revalidate exact revisions at
invocation, enforce limits and never accept lower-authority configuration overrides.
#1041/#1042 select Storage revisions and supply dataset/Snapshot rules separately.
#1046 supplies Storage Console administration and #1064 adds independent
StorageType administration plus the existing generic resource-grant journey.

## StorageType administration and resource grants

Publication accepts `{ version: /* complete StorageTypeVersionDescriptor */,
setPreferred: false }` and requires the current type `If-Match`. Publishing an
existing version returns `storage_type_version_exists` (409). Existing versions
remain immutable, and changing the preferred version never rebinds Storages.
Ordinary metadata edits preserve the existing version collection. Managed Core
and extension definitions remain protected, including publication and lifecycle.
A referenced type cannot be disabled or deleted (`storage_type_in_use`, 409).
The usages endpoint lists only dependencies discoverable by the current caller;
unlisted dependencies can still block lifecycle, without disclosing their identity.

Generic access overlays use the existing `/api/resource-access-policies` API and
its `/{resourceUid}/evaluate` operation. They remain independent from type/Storage
definitions and ETags. Creation without an ETag is allowed only when no overlay
exists; updating or deleting an existing overlay requires its current `If-Match`.
Absent/stale update tokens return 409 without overwriting saved grants.

Discover and Use are independent actions targeting exact descendant scopeRefs,
with explicit permitted propagation. Missing policies/actions deny descendant
access. Catalogues and exact metadata reads use the shared store's Discover
filter, including owner/namespace/kind and optional generation/version pins.
Same-name recreation receives a new UID and inherits no grants. The Console
creates UID-bound, unpinned Storage overlays so mutable metadata and additional
immutable versions preserve sharing; explicitly pinned policies fail closed
after the pinned revision changes. Synchronizers do not write access overlays.

The Access evaluator displays saved **generic** decisions, not unsaved draft
grants. ResourceAccessPolicy.Use is the current usage authority for Storage and
StorageType: a Tenant grant with IncludeDescendants permits its Workspaces;
Discover alone, revocation and foreign Tenant targets remain refused. Embedded
UsePolicy adds no second Storage-family gate while its administration is deferred,
as recorded in [ADR-0200](../decisions/0200-storage-execution-pins-protected-bindings-and-local-workspace-defaults.md).
Existing contextual authorization, scopes, UIDs/generations, versions/digests,
enablement, availability, configuration, mappings and capabilities still apply.
Connected Services retain their own checks. User-permission expansion is deferred to
[#1077](https://github.com/gbaudrit/agentstration/issues/1077), and UsePolicy
administration to [#1078](https://github.com/gbaudrit/agentstration/issues/1078).
The Instance-owned builtin uses functional `local@1.0.0`. There is no automatic grant, global exemption, ASP bypass
or per-Workspace type copy. Synchronization and restart preserve grants/revocations.

Type reads project `observedUse` with the authenticated consumer scope and the
shared evaluator's generic Use decision. This projection is recomputed and never
persisted. The Storage editor retains discoverable types for inspection while
disabling new selection when that generic grant is absent, independently of
provider availability. The server repeats its existing resolution checks.

## Bootstrap and persistence

Bootstrap handlers accept `StorageType` and `Storage` manifests at all three
scopes, with create/skip planning and normal governance. A Storage manifest must
explicitly pin an already materialized type UID/version/digests and, when needed,
service revision and export descriptors. Provision the type before preparing the
Storage manifest; bootstrap does not invent mappings or replace type references
by name.

Both resources use the existing scoped document store, with SQLite by default and
the same PostgreSQL document-store behavior. No EF migration, legacy Storage
contract or dual read/write is introduced. Other verticals and their histories
are preserved. See [ADR-0199](../decisions/0199-storages-own-explicit-service-export-bindings.md).

See [Console administration](storage-console.md) for scoped routes, explicit mapping,
reconciliation and UI delivery boundaries.

See [Storage execution](storage-execution.md) for the provider-neutral content port,
functional local version, Workspace defaults, AEP/ASP bindings and Docker persistence.
