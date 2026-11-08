# Storage vertical

FR #1045 adds a resource family independent of Data Sources, Knowledge, Connected
Services and provider transport. [The API reference](../../reference/storages.md)
defines routes and contracts; [ADR-0199](../../decisions/0199-storages-own-explicit-service-export-bindings.md)
records ownership and authority decisions without changing accepted ADRs.

| Project | Owns | Allowed boundaries |
|---|---|---|
| `Agentstration.Storage.Contracts` | StorageType/Storage resources, exact revision evidence, manual bindings, protected resolution and availability/usage ports | Resources, generic ResourceManagement, ConnectedServices.Contracts |
| `Agentstration.Storage` | Immutable versions, lifecycle, validation, use/access authorization, reconciliation, resolution | Contracts, Identity.Contracts, ResourceManagement.Contracts, bounded Schemas validation |
| `Agentstration.Storage.Api` | HTTP mapping, ETags, pagination, Problem Details | Storage services and family-neutral API contracts |
| `Agentstration.Infrastructure` | Composition, local metadata availability, builtin type provisioning, Bootstrap handlers | Existing inward-facing ports and document store |

There is no Storage-specific relational schema. ResourceManagement's existing
document stores preserve exact scope, UID, governance and optimistic concurrency.
The family never references EF, ASP, AEP, physical provider SDKs, Console, Data
Sources or Knowledge. Extension materialization uses `SynchronizeManagedAsync`
under exact trusted governance; #1040 owns the actual AEP mapping adapter.

The three authority layers remain separate:

1. Connected Service owns connection options, Parameter/Secret references and typed
   exports. Its metadata inspector validates exports without resolving Secret values.
2. Storage owns implementation selection, explicit requirement/export mappings,
   bounded configuration and a fixed envelope.
3. Data Source/Knowledge own logical dataset or output rules and exact selected
   Storage revisions. They receive no ability to replace connection or envelope.

Resolution repeats current authorization and contract checks. It verifies both
descendant UsePolicy and the resource access policy for each dependency. Protected
configuration does not serialize with the logical revision. Sharing physical
placement or an extension never grants shared logical authority.

Reconciliation observes drift without rebinding. Accepting a new complete
definition requires an explicit request and ETag; dependencies prevent destructive
rebinding or deactivation. Missing providers leave stored resources and evidence
unchanged. Read-time observations do not create writes or change ETags.

The Core `local` type supports management/resolution without a Connected Service.
Its immutable metadata-only `1.0.0` remains unchanged. #1040 adds functional
`2.0.0`, exact per-Workspace defaults, separate UID-bound directories and protected
ASP/AEP/Artifact adapters. [Execution and deployment](../../reference/storage-execution.md)
and [ADR-0200](../../decisions/0200-storage-execution-pins-protected-bindings-and-local-workspace-defaults.md)
describe runtime authority, policy preservation, current capability limits and
historical Artifact handling. [#1046 supplies the Console](../../reference/storage-console.md);
#1041/#1042 retain dataset and Snapshot ownership.

Fast tests exercise domain and authority invariants with deterministic ports.
Integration tests cover real HTTP/Problem Details, Bootstrap, Secret non-resolution,
SQLite restart and concurrency. Architecture tests enforce project direction and
keep provider/transport implementations outside the family.

## Validation evidence — 2026-11-08

Verified with .NET SDK 10.0.300 against implementation commit `1610290` on
`feature/fr-1045-storage-api`, based on `integration/fr-1032` at `09de3fc`.

| Check | Result |
|---|---|
| Solution restore | Passed; all projects restored |
| Release build without restore | Passed; 0 warnings, 0 errors |
| Required Fast lane | 969 passed, 0 failed, 0 skipped |
| Required Integration lane | 1069 discovered: 1057 passed, 0 failed, 12 optional tests skipped |
| LikeC4 and ADR validation | Passed; 3 model files and 197 unique ADRs |
| Production Docusaurus build | Passed |
| Isolated Development bootstrap/server smoke | Health 200, local fixture login 204, type catalog 200, Storage creation 201 and exact detail 200/ready |

Required commands were run from the repository root:

```text
dotnet restore Agentstration.slnx
dotnet build Agentstration.slnx --configuration Release --no-restore
dotnet test --solution Agentstration.Tests.Fast.slnx --configuration Release --no-build --minimum-expected-tests 777 --max-parallel-test-modules 4
dotnet test --solution Agentstration.Tests.Integration.slnx --configuration Release --no-build --minimum-expected-tests 954 --max-parallel-test-modules 2
```

The 12 skips are existing opt-in integrations: 2 PostgreSQL, 8 live Foundry,
1 llama.cpp and 1 LocalAI. No Storage test was skipped. The final Integration
pass completed after an environment restart interrupted an earlier attempt.
There is no Storage Console change or new browser journey in #1045.

The Console client and components reference only `Storage.Contracts`. The two
existing composition roots register `IStoragesClient` over the product HTTP API.
The UI preserves exact pins in drafts and separates metadata PUT, observation-only
reconciliation and confirmed complete replacement. It never invokes ASP/AEP or
Secret-resolution ports. Architecture checks explicitly permit this family contract
while continuing to reject Storage services, API modules and concrete persistence
from Console assemblies. This follows ADR-0199 and the existing Connected Service
Console architecture; it introduces no new architectural decision or ADR.

## Execution boundary

`Storage.Contracts` owns content DTOs, restrictions and `IStorageContentGateway`.
`Storage` still references no ASP/AEP implementation. Infrastructure composes the
SDK adapters and the native local backend; the optional independent `Asp.Aep`
library configures extension bindings without product-resource references.

Every content call reauthorizes exact resource/type/service/export pins plus the
current execution permission. Pure local envelope validation participates in
management through a narrow port, without Secret resolution or physical I/O.
Remote configuration layers travel through authenticated bounded AEP exchange;
content Tool arguments carry only logical target selection and business inputs.

Initialization target 3 reconciles existing Workspaces through the existing fenced
startup mechanism. Defaults and first-grant markers are durable; subsequent
provisioning preserves configuration, disablement and revocations. Operational
defaults retain logical administrative ownership and exact operational access.

New Artifact receipts pin their Storage UID/revision. Existing Artifact locations,
receipts, leases and histories retain their original adapter. Receipt dependencies
match exact UIDs; another resource at the same name does not inherit them.

## FR #1040 validation — 2026-11-08

Execution behavior was verified against implementation `2746894` on
`feature/fr-1040-storage-adapters`, based on `integration/fr-1032` at `a27d3d7`.
The PR remains a draft pending the default-provisioning scope clarification below.

| Check | Result |
|---|---|
| Solution restore and Release build | Passed; 0 warnings, 0 errors |
| Required Fast command | 999 passed, 0 failed, 0 skipped |
| Required Integration command | 1,087 discovered: 1,075 passed, 0 failed, 12 optional tests skipped |
| Independent AEP SDK conformance | 59 passed, 0 failed, 0 skipped |
| Optional `Agentstration.Asp.Aep` packaging | Passed; README, license and notices included |
| ADR/LikeC4 and production Docusaurus build | Passed; 198 unique ADRs |
| Standalone Development executable | Health 200, fixture login 204, StorageType catalogue 200 and Storage list 200; local versions 1.0.0/2.0.0 and Instance default present |
| Real Docker remove/recreate | Passed; separate write/read containers retained two volumes, exact Workspace/Storage identities, content reference, Artifact receipt and historical Artifact content |

Both functional commands used the repository's required minimum test counts
(777/954) and maximum parallel module counts (4/2), without disabling analyzers
or NuGet audit. The 12 skips remain the existing opt-in PostgreSQL/live-model
integrations; no Storage execution test was skipped. Focused checks cover native
filesystem isolation/restart/idempotency, the actual authenticated AEP/MCP exchange,
late Secret grant redemption/redaction, narrowed provider envelopes, operational
ownership, concurrent provisioning, collision safety and atomic Artifact copies.

Docker used a locally assembled offline .NET 10.0.8 runtime image and the published
`StorageVolumeProbe`, executing both phases with networking disabled. Pulling
`mcr.microsoft.com/dotnet/aspnet:10.0` was refused by the registry in this execution
environment. The checked-in Dockerfile using that official base was therefore not
validated here; this does not replace the actual retained-volume container test.

The updated #1040/#1064 scope defers new user-permission work to #1077 and UsePolicy
authoring/composition to #1078. The current candidate initializes exact, recorded
UsePolicy grants once for Core defaults, in addition to generic Discover/Use;
it never globally exempts the type or restores revoked grants. Whether this
initialization belongs in #1040 or must instead leave affected defaults blocked
is awaiting maintainer clarification. Existing restrictive checks stay in force;
a generic grant alone does not imply usable Storage. This validation is not a
scope approval, UX approval or merge. No reusable browser journey was added.
