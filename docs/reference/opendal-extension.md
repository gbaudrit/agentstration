# OpenDAL Storage extension

The optional standalone extension implements FR #1039 under #1032. It contributes
Storage types through AEP and executes ASP content operations over MCP through the
existing server SDK. It has no dependency on Agentstration business resources and
does not create Connected Services, infer mappings or publish Agent Tools.

## Implemented types

| Type | Version | Execution | Connection requirements |
| --- | --- | --- | --- |
| opendal.fs | 1.0.0 | describe/list/stat/read/write | None |
| opendal.s3 | 0.1.0-preview.1 | Descriptor only; every operation unavailable | endpoint, access-key; secured secret-key |
| opendal.azblob | 0.1.0-preview.1 | Descriptor only; every operation unavailable | endpoint, account; secured account-key |

The remote descriptors advertise no executable capabilities and refuse binding
configuration before reading Secret values. Their typed contracts document future
explicit mappings; neither contributes a Connected Service type or instance.
The AEP bound-value/Secret-access protocol capabilities describe the protected
requirement format, not operational remote storage support.

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

For a future executable remote backend, an administrator must select a separate
Connected Service and explicitly map its typed exports to each requirement.
Connection values, implementation configuration, Storage envelope and dataset
policy remain separate. The current S3/Azure descriptors cannot execute that path.

## Storage semantics and limits

Each logical StoreId/BindingId pair receives its own hashed directory beneath the
operator root. Two Storages share a filesystem service without sharing data or
reference authority. Logical keys are stored inside the bounded state object and
are never interpreted as physical paths.

All content and receipt state is read/written through OpenDAL. It uses one atomic
state object and the filesystem backend's same-filesystem atomic write directory;
an exclusive coordination lock serializes cooperating processes. Native operator
handles are disposed after each operation. Native errors are redacted into bounded
ASP errors. No blind retry loop is added.

- Profile ceilings: 100 objects/page, 65,536 bytes/read or write; envelopes can reduce them.
- Per store: 128 objects, 8 MiB decoded content, 512 live idempotency receipts,
  16 MiB serialized state. Four native operations may run concurrently per host.
- Missing expectedVersion means create-only; replacement requires the current version.
  Exact retries return the original receipt even after replacement or process restart.
- Original deadlines remain mandatory; expired requests cannot replay. Full
  original identities, arguments and authorized context must remain unchanged.
- Cancellation while waiting for coordination stops without writing. Native
  filesystem commit is synchronous and bounded; cancellation after commit reports
  uncertainty. It never releases the lock while native work continues.
- Re-list after restart to reacquire process-bound ASP object references/cursors.
  Pagination is a live ordinal walk, not an immutable snapshot.

This state-object layout is intended for bounded local storage, not importing an
existing arbitrary directory or high-throughput object hosting. Updates rewrite
the bounded state. No deletion, historical versions, Snapshot retention,
cross-host distributed coordination or power-loss durability is guaranteed.
The root must be operator-owned and inaccessible to untrusted filesystem writers;
symlink checks do not eliminate races caused by a privileged administrator.

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

See [ADR-0201](../decisions/0201-opendal-runs-in-an-autonomous-asp-extension.md).
The dedicated OpenDAL CI publishes and smoke-tests native distributions on Linux
and Windows; the root Integration lane includes the native provider/MCP tests and
the actual product-to-extension Storage execution regression.
