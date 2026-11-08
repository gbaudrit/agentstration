# Storage execution and local persistence

Storage management is documented in [Storages](storages.md). FR #1040 adds the
execution adapters consumed by Data Source and Knowledge integrations. It adds no
Console journey or public content-management endpoint.

## Consumer contract

`IStorageContentGateway` belongs to `Agentstration.Storage.Contracts`. Select an
exact `StorageRevisionReference`, the trusted consumer scope/address and a
`StorageContentPolicy`. Each call validates current permissions and the complete
saved binding again. Use `runs/execute` for actual content invocation in addition
to Storage discovery/use and the consuming feature's own authorization.

Supported operations are bounded list, stat, read and conditional/idempotent write.
Rules can narrow operation rights, prefix, key range and payload/page bounds. They
cannot replace the connection, type, configuration or Storage envelope. Connection,
configuration and envelope stay protected and distinct. Unsupported constraints
fail explicitly. Errors are bounded and redact provider messages and locations.

A write without `ExpectedVersion` creates only. Replacement requires the exact
current version. Keep the original request ID, deadline, content and restrictions
when retrying an uncertain write. Changed arguments under the same request identity
fail. Reads/stat require a protected object reference; pagination cursors also pin
the original query. Reauthorization and exact resource checks run on every call.

## Builtin local type

There is one Instance-owned `local` StorageType. Immutable `1.0.0` remains
metadata-only. Version `2.0.0` implements list/stat/read/write; existing Storages
remain on their chosen version until explicit reconciliation.

Configuration is `{}`. The local envelope uses `StorageContentPolicy` fields:
`list`, `stat`, `read`, `write`, `prefix`, `maximumPageSize`, `maximumReadBytes`,
`maximumWriteBytes`, `keyFrom` and `keyBefore`. Missing fields use the contract
defaults; write defaults to false. Both schema and pure local restriction
validation run during management, without resolving a Secret or touching content.

Limits are 100 items/page, 65,536 bytes/read or write, 1,024 current objects,
64 MiB total current content and 512 outstanding write receipts per Storage.
Expired receipts are pruned at a subsequent write. Capacity refusal is explicit.
Overwritten object versions become unavailable; no stable-version or durable
Snapshot retention capability is advertised.

Provisioning attempts a `local-default` using the existing Storage service and its
current generic Use authorization and exact resource checks. It creates no grants, writes no UsePolicy
and changes no access overlay. An existing default retains its UID, enabled state,
configuration, envelope and policies; a reserved-name/ownership collision fails.

The single Instance-owned type starts with no automatic descendant sharing.
Workspace/Tenant defaults remain unavailable until an administrator explicitly
grants generic Use. A grant to a Tenant with propagation permits its Workspaces;
Discover alone is insufficient. Empty embedded UsePolicy metadata creates no
second lock while #1078 is deferred. The Instance operational
default works in its logical Instance context. No type copies, global exemptions
or automatic sharing are introduced to make a blocked default usable.

A blocked attempt returns `unavailable` with a bounded diagnostic and writes a
structured operator warning containing Workspace/scope/code, without failing the
rest of Workspace initialization. Existing defaults are observed without repair
or mutation. Generic Use is the current Storage-family authorization contract;
embedded UsePolicy metadata is not an additional gate. #1064 owns
existing generic grant administration, #1077 new user-permission work and #1078
UsePolicy work; this increment implements none of those deferred policy features.

## Operator configuration and Docker

The default root is `<Data:Directory>/storages`, which is `/data/storages` in the
standard Docker image. Override it using
`Agentstration:LocalStorage:RootDirectory`, or environment variable
`Agentstration__LocalStorage__RootDirectory`.

For example, retain `/data` for metadata, keys and historical Artifact content and
mount a separate local content root:

```sh
docker run --name agentstration \
  --mount type=volume,src=agentstration-data,dst=/data \
  --mount type=volume,src=agentstration-storages,dst=/storage \
  -e Agentstration__LocalStorage__RootDirectory=/storage \
  -p 8080:8080 agentstration:local
```

A bind mount can replace either named volume. The process user needs read/write
and directory traversal permission. Keep the roots operator-owned, unavailable to
ordinary application callers and protected against concurrent filesystem edits.
Symbolic/reparse links are refused. Separate Storage UIDs receive separate
directories, including two Storages within one Workspace. Physical names are not
part of ordinary resource/Tool results or diagnostics.

Persist both metadata and bytes when replacing a container. Losing metadata creates
different UIDs; retained directories are never automatically adopted by name.
The independent builtin Artifact backend keeps `/data/durable-artifacts`.
When no selectable default exists, new builtin Artifact writes retain that
existing backend. Workspace-scoped Artifact Flows also retain it in operational
Workspaces: they do not implicitly select the administrative default owned at
Instance/Tenant scope. This neither grants Storage access
nor retargets a selected reference. When an authorized default is selected, new
receipts pin its exact Storage revision and use its directory; disabled, revoked,
stale or incompatible selections fail without falling back. Backup/restore must preserve these
locations and resource/receipt identities together.

The native Artifact adapter uses the logical key `artifacts/<object-id>` and
respects the selected Storage envelope, including read/write/stat permissions,
prefix/key range and per-call payload limits. Copies use a pending file; only a
complete file matching the pinned length and SHA-256 is atomically promoted.
Readers never see pending content. A retry verifies an existing committed file
and cannot truncate or overwrite it. At most 128 unfinished copies can be staged
per Workspace/Storage directory; retrying the same receipt recovers its pending
copy. Pending files are not deleted automatically. Operators must preserve active
copy state and retained content during recovery.

The explicit deployment probe under `tests/fixtures/StorageVolumeProbe` runs the
production module composition without a model, provider account or UI. It verifies
that the business default stays blocked under existing restrictions, then uses
the authorized Instance default in its operational context. Its write/read phases
verify exact default UID, content reference, Artifact receipt and historical
Artifact content across separate processes/containers, without creating grants. It is not a
product endpoint or a silently skipped functional test.

Build and run the deployment check from the repository root:

```sh
dotnet publish tests/fixtures/StorageVolumeProbe/StorageVolumeProbe.csproj -c Release -o artifacts/storage-probe
docker build -f tests/fixtures/StorageVolumeProbe/Dockerfile -t agentstration-storage-probe artifacts/storage-probe
python3 scripts/ci/test-local-storage-volume.py --image agentstration-storage-probe
```

The script creates only uniquely named test volumes/containers and removes them
afterward. It removes the write container before creating the read container and
retains both volumes between phases. It does not use an external model or service.

## AEP/ASP extension contract

An extension advertises `aep.storage-types` version `1.0` and versioned
`StorageTypes` contributions. Each includes exact schema digests, typed/protected
requirements, actual capabilities/limits and an `asp.content/1` MCP mapping.
Requirements use the existing bound-values/Secret-access capabilities as needed.
Enrollment keeps the exact registration UID/generation, extension namespace and
contribution digest. An unchanged version cannot absorb provider drift.

The product rechecks the live authenticated manifest before invocation. Only saved
manual export bindings are resolved. The protected authenticated
`POST /aep/storage/bindings` exchange carries connection grants, opaque configuration,
opaque envelope and consumer restrictions separately. A short-lived lease selects
the configured target through `X-Agentstration-Asp-Binding`; MCP business arguments
remain the SDK's `{ store, arguments }` envelope.

The optional independent `Agentstration.Asp.Aep` library provides bounded lease
handling and the configuration endpoint. Its `IAspAepBindingFactory` must validate
provider-specific schemas/envelopes, resolve AEP grants privately and authorize the
actual authenticated caller independently on every operation. Explicitly authorize
which callers may configure bindings. Use the declared immutable provider ID,
version and schema. The provider may further narrow the requested operation,
prefix, range and payload rules when applying its opaque envelope; widening any
restriction is refused. No product resource assembly or other extension is required.

Originating user/Agent authority is checked in the product; the extension checks
its actual service or direct consumer identity. An opaque authorization revision
partitions references but is not a delegation credential. Providers needing actual
end-user delegation must refuse unsupported service-identity execution.

The SDK's static bindings remain usable directly by authorized consumers. A target
selector, forged subject field or possession of another caller's lease never
grants authority. Configuration grants expire within 30 seconds, capacity is 128
leases, configuration payloads are bounded, and Secret capabilities are revoked
when the product invocation ends. No operation is automatically published as an
Agent Tool; optional fixed-target exposure belongs to #1044.

## Remaining integrations

- #1064: StorageType administration and existing generic scoped resource grants;
  #1065: its gated reusable UX scenario. #1077 owns new user-permission work and
  #1078 owns UsePolicy authoring/composition; current restrictive checks remain.
- #1041: dataset/acquisition selection, rules, consumer authorization and execution.
- #1042: projection, freshness, publication and retrieval. Durable Snapshots require
  real stable-version/retention guarantees beyond the current content profile.
- #1039: OpenDAL and provider packaging. It consumes this contract without a direct
  Connected Service-extension dependency.
- #1033: integrated UX scenario, after its existing implemented-UX approval/merge gate.
