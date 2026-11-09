# OpenDAL Storage extension

The optional standalone extension implements FR #1039 under #1032. It contributes
Storage types through AEP and executes ASP content operations over MCP through the
existing server SDK. It has no dependency on Agentstration business resources and
does not create Connected Services, infer mappings or publish Agent Tools.

## Implemented types

| Type | Version | Execution | Connection requirements |
| --- | --- | --- | --- |
| opendal.fs | 1.0.0 | Four text/blob methods, list/stat, session controls | None |
| opendal.s3 | 1.0.0 | Same generic ASP group through native S3 Operator | endpoint, region, access-key; secured secret-key |
| opendal.azblob | 1.0.0 | Same generic ASP group through native Azure Blob Operator | endpoint, account; secured account-key |

All types handled by this extension are contributed and executable. Other services
compiled into Apache.OpenDAL are not integrated StorageTypes. Adding one requires
its typed connection/configuration contract and deterministic adapter validation;
no new Agent-facing method or physical storage engine is needed.

Remote configuration is `{ "bucket": "...", "root": "optional-prefix/" }` for S3
or `{ "container": "...", "root": "optional-prefix/" }` for Azure Blob. Connection
values come only from explicit typed Connected Service exports. Credentials use
existing AEP one-use Secret grants, with redirects disabled during redemption.
Endpoints require HTTPS, except loopback HTTP for local deployments/tests. S3
ambient configuration and instance-metadata credential loading are disabled.

Filesystem configuration is exactly `{}`. The operator supplies the physical
root through `OpenDal:RootDirectory`, never through Storage configuration or MCP
arguments. An example Storage envelope is:

```json
{
  "prefix": "notes/",
  "list": true,
  "stat": true,
  "read": true,
  "write": true,
  "maximumPageSize": 10,
  "maximumReadBytes": 4096,
  "maximumWriteBytes": 4096
}
```

Omitted envelope write permission defaults to false. Optional ordinal `keyFrom`
and `keyBefore` further restrict keys. Dataset policies may narrow these values;
they cannot change physical placement or widen the envelope.

## Build and offline demonstration

Use the repository SDK from global.json. No Rust compiler, cloud service, model
or downstream credentials are required. The official NuGet package is pinned in
Directory.Packages.props; package restore requires access to NuGet or a populated cache.

```sh
dotnet build samples/OpenDalSmoke --configuration Release
dotnet publish src/Agentstration.Extensions.OpenDal --configuration Release --runtime linux-x64 --self-contained false --output artifacts/opendal/linux-x64
python scripts/opendal/smoke.py --publish-dir artifacts/opendal/linux-x64
```

Use `win-x64` in both paths/runtime arguments on Windows. The smoke runner
generates a temporary key without displaying it, starts only the published
extension, configures an authorized lease and performs real MCP write/retry/list/
stat/read calls. It restarts the process with the same filesystem root, reacquires
references and reads the persisted content. It also checks native binaries and
upstream notices in the publish output.

For a long-lived standalone instance, provision a protected one-line shared key
with the existing AEP procedure, and configure:

```text
ASPNETCORE_URLS=http://127.0.0.1:5189
OpenDal__RootDirectory=<absolute private persistent directory>
OpenDal__AllowedClients__0=agentstration-development
Aep__EnrollmentMode=SharedKeyFile
Aep__SharedKeyFile__Path=<absolute protected shared-key file>
Aep__SharedKeyFile__ClientId=agentstration-development
```

Then run the published Agentstration.Extensions.OpenDal.dll. The shared key is a
transport credential; it is not a backend connection requirement. PairingCode
enrollment is also provided by the existing AEP SDK; explicitly allow the assigned
transport client identity in OpenDal:AllowedClients. Do not allow arbitrary
authenticated clients. Production transport must use the existing protected AEP
deployment/authentication practices.

## Use from Agentstration

1. Register/enroll the extension with expected identity
   `agentstration.extensions.opendal` and its authenticated endpoint.
2. Existing enrollment synchronizes its exact contributed StorageTypes under
   namespace `agentstration.extensions.opendal`. It preserves provider UID,
   generation, contribution version and digest.
3. Create a scoped Storage selecting `opendal.fs@1.0.0`, exact type UID/schema/
   contract digest, configuration `{}` and the desired fixed envelope. No
   Connected Service or requirement mapping is needed for this offline backend.
4. Use the existing authorized Storage content gateway. The product revalidates
   resource identity, scope and generic Use, then configures a short-lived
   protected ASP binding and invokes the known MCP operation group.
5. Provider authentication, allowlist, exact StoreRef, envelope and per-operation
   checks remain mandatory. Disablement, revoked access and stale identities fail.

For S3/Azure, select a separate Connected Service and explicitly map its standard
and secured exports to every requirement shown above. The extension contributes
no Connected Service type or instance and does not select another extension.
Connection values, bucket/container configuration, Storage envelope and dataset
policy remain separate. Agents continue to see generic logical content operations.

## Storage semantics and limits

Each logical StoreId/BindingId pair receives an isolated SHA-256 partition. Filesystem
placement is `<operator-root>/<partition>/data/<encoded-key>.blob`. Keys use
reversible lowercase UTF-8 hex split into bounded path components, preserving exact
logical names while avoiding Windows case, reserved-name and trailing-dot aliases
across ordinal authorization boundaries. This is one physical object per key, with
no lookup index. Remote keys retain their original spelling and add the partition
to the configured bucket/container root. A logical key does not imply a filesystem. There is no `objects.json`, shared
content serialization, object/version index or persisted protocol receipt ledger.
Filesystem-specific containment/link checks protect the local mapping; all actual
content I/O uses the same OpenDAL Operator/Stream methods as remote backends.

ASP.Server supplies [text/blob sessions](https://github.com/gbaudrit/agentstration/blob/integration/fr-1032/asp/docs/protocol.md),
strict UTF-8, fixed byte ranges, sequencing, replay, expiry and explicit finalization.
Protected AEP binding leases still last at most 30 seconds. Longer direct SDK
transfers must renew the protected lease with the same exact target and authority;
a lease is not extended by an ASP session. The current product content port remains
bounded to one returned payload.

The extension translates authorized operations and native capabilities. It exposes
no physical settings in content arguments and adds no blind retry loop.

- Profile ceilings: 100 objects/page and 65,536 decoded bytes/chunk, narrowed by
  effective policy. Objects can span many chunks; no 8 MiB or 128-object store quota.
- This host allows 32 open SDK sessions and 32 native operation/transfer handles.
  Default replay limits remain 8,192 entries, 64 MiB decoded read replay budget,
  30 seconds/chunk, within the original at-most-five-minute session deadline.
- Effective `describe.transfers` comes from `Operator.Info.Capability`, with SDK
  options narrowed to what this binding can uphold. Progressive transfer, empty
  writes, create-only, replacement, ETag conditions and metadata are independent
  from retention. All three types implement native list/stat/read/write.
- `mode: create` uses native IfNotExists. `replace` is explicitly unconditional
  unless a supported native ETag condition is provided. Native version write
  conditions have no managed 0.3.1 option and are rejected. The SDK's combined
  ETagConditions flag requires both IfMatch and IfNoneMatch; S3's partial native
  condition support is conservatively not advertised under that combined flag.
- Append is declined: the managed Stream completion API returns no Metadata, so
  an exact resulting object length after append cannot be evidenced. Metadata is
  accepted only when all declared metadata fields have native support.
- Only confirmed native completion yields SDK success. Stream receipts preserve
  known bytes/requested metadata with unavailable version/ETag/provider digest
  left null; the SDK computes the transferred-byte SHA-256. No later stat pretends
  to be the original commit. A later list/stat can supply native ETag/version for
  subsequent protected references, independently of that original receipt.
- No atomic publication is advertised. For filesystem creates, do not enable
  atomic_write_dir: the native mode failed concurrent create-only exclusion in
  our regression test. Direct native create_new/O_EXCL enforces exclusion; an open
  write may be progressively visible. No custom cross-process CAS lock is added.
- Abort is cleanup, not rollback. Native Stream disposal may close/publish unfinished
  work; the extension returns unknown status for such writes. Failed finalization
  does not invent a receipt. Native handles stay alive until submitted work settles,
  even when the request is cancelled. Native operation/I/O timeouts are 20/15 seconds.
- Replay receipts are process-local SDK state. Exact live retries return their
  original result even after later replacement. Restart preserves physical content,
  not sessions/receipts; reacquire list/object references after restart. No durable
  exactly-once promise, historical-version retention or Snapshot retention is made.

The 0.3.1 .NET `List` API materializes native entries. ASP pages are ordinal, filtered
and bounded, but large namespace listing can use memory proportional to those
entries; a native iterator binding would be needed for bounded listing memory.
Pagination is live rather than a snapshot. Blob content transfers themselves use
bounded buffers without whole-object materialization. No deletion, cross-host
transaction or power-loss durability guarantee is provided by the extension.

The filesystem root must be private and operator-owned. Reject symbolic, reparse
and dangling links; checks do not prevent a privileged concurrent filesystem
replacement. Persist product identities/control-plane data alongside physical
content to keep exact configured targets resolvable.

## Native packaging and deployment

Apache.OpenDAL 0.3.1 includes net8.0/net10.0 assemblies and native libraries for
win-x64, linux-x64, linux-arm64 and osx-arm64. The initial extension validation
matrix targets Windows x64 and Ubuntu 24.04 Linux x64. Other RIDs in the package
are not an extension support claim. Linux requires glibc and libgcc; musl/Alpine
is unsupported. Do not enable trimming, single-file publication or Native AOT.

The RID publish includes the managed wrapper, matching native library and
`licenses/opendal/` with Apache/MPL/CC0 licenses, NOTICE and the upstream README
containing third-party source links. No upstream source code was copied from
Xuanwo/mcp-server-opendal; it is an implementation reference, not our ASP endpoint.
The native and managed binding are consumed together from the official package.

`deploy/opendal/Dockerfile` builds the optional Linux glibc distribution. Mount
a persistent directory at /data writable only by the container UID and mount the
shared key read-only. Mount and preserve product control-plane identities too.
Never expose physical configuration as MCP business arguments. Building for
linux-arm64 requires an appropriate build environment and separate validation.

See [ADR-0206](../decisions/0206-opendal-runs-in-an-autonomous-asp-extension.md).
The dedicated OpenDAL CI publishes and smoke-tests native distributions on Linux
and Windows; the root Integration lane includes the native provider/MCP tests and
the actual product-to-extension Storage execution regression.


## Validation scope

The native extension tests exercise real MCP, filesystem writes/reads, concurrent
native conditional creation, more than 128 keys, a 9 MiB progressive transfer,
strict authority, restart and exact replay. S3 and Azure tests execute the actual
native operators against synthetic loopback HTTP protocol peers, including secured
AEP credential redemption, progressive writes, ETag-protected ranged reads and
create conflicts. They are not a full emulator or live cloud certification. The
Management API regression covers contributed StorageTypes, explicit typed export
mappings, product content calls, isolation, disablement and revocation. The published
standalone smoke checks matching native assets/licenses and content after restart.
