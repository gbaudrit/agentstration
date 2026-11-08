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

Resolution repeats current authorization and contract checks. For Storage and
StorageType resources, the generic Use decision is the current usage authority;
Tenant propagation, exact identities, revocations and scope boundaries are checked
on each call. Embedded UsePolicy metadata does not impose a second Storage-family
lock while #1078 is deferred. Connected Service inspection retains its own checks. Protected
configuration does not serialize with the logical revision. Sharing physical
placement or an extension never grants shared logical authority.

Reconciliation observes drift without rebinding. Accepting a new complete
definition requires an explicit request and ETag; dependencies prevent destructive
rebinding or deactivation. Missing providers leave stored resources and evidence
unchanged. Read-time observations do not create writes or change ETags.

The Core `local@1.0.0` type supports management/resolution and bounded content
without a Connected Service. Before release, the maintainer retained a single
functional initial version rather than intermediate compatibility contracts.
#1040 supplies guarded default provisioning, separate UID-bound directories and protected
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

## StorageType administration (#1064)

The existing family now exposes explicit immutable-version publication and a
bounded discoverable-reference projection. Dependency checks cover type deletion
and disablement even when the consumer cannot be disclosed. Console adds an
independent catalogue, exact details, metadata editor and version-publication
form through `IStorageTypesClient`, implemented by the existing HTTP client and
registered in both Console composition roots. There is no new module or schema.

The shared Access panel reuses the canonical evaluator for saved generic
Discover/Use decisions. Ordinary Storage overlays bind to exact resource UID,
scope, namespace and kind rather than automatically pinning mutable generation.
Explicit revision pins remain supported and fail closed in both store discovery
and effective evaluation. Existing-overlay writes require the policy's own ETag;
conflicts retain the Console draft and require explicit review of the current
revision before retry. SQLite and PostgreSQL implement the same identity and
concurrency rules. Managed-resource synchronization never writes these overlays.

These changes implement ADR-0172/ADR-0181 and the Storage family contract corrected
by ADR-0200. Generic ResourceAccessPolicy.Use is the current Storage/StorageType
usage authority; embedded UsePolicy adds no second gate while #1078 is deferred.
Existing caller-authorization checks remain in place; new user-permission work is
tracked by #1077. Accepted ADRs remain unchanged. #1040 owns ASP/AEP/Artifact/local
execution and provisioning; #1041/#1042 retain consumer rules. #1065 remains
blocked on explicit implemented-UX approval and implementation merge, independently
of the required implementation smoke/regression checks.

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
startup mechanism. Provisioning writes no access grants or UsePolicy and preserves
configuration, disablement and existing access decisions. Absent generic Use grants may block Workspace/Tenant defaults; a bounded result/operator warning
reports that limitation while other provisioning continues. The Instance default
is usable in its logical administrative context. #1077/#1078 retain new permission
and policy work.

Authorized selected-Storage Artifact receipts pin their Storage UID/revision.
The independent Artifact backend remains the builtin choice when no default can
be selected in the Flow's Workspace context. A selected reference never falls back
after disablement or revocation. Existing locations, receipts, leases and histories
retain their original adapter. Receipt dependencies
match exact UIDs; another resource at the same name does not inherit them.

## FR #1040 scope correction and validation — 2026-11-08

The maintainer confirmed that policy work remains out of #1040. Automatic grant
initialization was removed, including type UsePolicy mutation, operational Storage
UsePolicy grants, generic grant initialization and first-grant markers. Existing
generic policy evaluation remains the authority. The maintainer then clarified
that generic Use must authorize the complete Storage-family path, without a second
unadministrable embedded UsePolicy lock. That extra Storage/StorageType gate was
removed at the application boundary; no grants are synthesized. Workspace/Tenant
defaults without generic authorization are reported as unavailable; existing Artifact functionality stays usable
through its independent backend. No permission exception or scope approval is
pending. #1077/#1078 retain their deferred requirements.

Validation was completed against application correction `c90f80f`, based on
`integration/fr-1032` at `a27d3d7`, with .NET SDK 10.0.300. Earlier candidate counts
are superseded by these results.

| Check | Result |
|---|---|
| Root restore and Release build | Passed; 0 warnings, 0 errors; audit/analyzers enabled |
| Required Fast lane | 999 passed, 0 failed, 0 skipped; minimum 777, maximum 4 parallel modules |
| Required Integration lane | 1,090 discovered: 1,078 passed, 0 failed, 12 existing optional skips; minimum 954, maximum 2 parallel modules |
| Focused Storage module | 31 passed |
| Focused HTTP/provisioning cases | 6 passed |
| Real SQLite sharing/revocation/restart scenario | Passed; native content and exact identities retained, foreign Tenant refused, synchronization/restarts preserve grants and revocation |
| ADR/LikeC4/full Docusaurus build | Passed; 198 unique ADRs |
| Actual standalone executable | Health 200, Development fixture login 204, Storage catalogue/list 200; builtin versions and Instance default verified |
| Real Docker remove/recreate | Passed; two retained volumes preserve exact Storage/Workspace identities, protected content reference, Artifact receipt and historical content without grant initialization |

No Storage test was skipped. The 12 skips are the existing optional PostgreSQL and
live-model integrations. An initial lane attempt exposed an existing Git test's
exact cancellation-type assertion; isolated test correction `4840c11` accepts valid
derived .NET cancellation exceptions, and the complete required lane was rerun.
The Git provider implementation is unchanged.

The AEP SDK and ASP/AEP package source are unchanged by the authorization correction;
independent checks passed earlier (59 AEP tests and package README/license/notices).
Docker uses the published production-composition probe in a locally assembled
.NET 10.0.8 runtime image with networking disabled. The official
`mcr.microsoft.com/dotnet/aspnet:10.0` image pull was refused by the registry in this
environment; its checked-in Dockerfile remains unverified here.

Cross-branch review of #1079 confirmed that its selector combines generic Use,
enablement and version availability. Its former builtin test expecting a
UsePolicy-related 403 must expect successful authorized creation, and must select
an exact/preferred version rather than assuming the builtin has one version. Its
Storage-specific help text also needs alignment with the current contract. An
isolated test patch was prepared for that branch; no Console source was changed
in #1080. Combined-branch/browser validation is not claimed by these backend checks.
No new reusable UX scenario was added; #1033/#1065 retain their existing gates.

The #1064 integration applies those cross-branch adaptations and the subsequent
maintainer decision to retain one functional pre-release `local@1.0.0`. The
Storage-specific help, builtin version selection, authorized creation/revocation
assertions and request-observation/selector regressions now follow ADR-0200.
Combined validation and the isolated service-fixture correction are recorded in
[the Console reference](../../reference/storage-console.md#validation-evidence--combined-10641040-2026-11-08)
and PR #1079. These checks do not approve the UX or authorize a merge.

## OpenDAL extension — FR #1039

The autonomous Agentstration.Extensions.OpenDal host consumes Apache.OpenDAL 0.3.1
through ASP.Server/ASP.Aep. It contributes executable opendal.fs@1.0.0 and
non-executable S3/Azure preview descriptors. Product modules retain their existing
provider-neutral ports and never acquire native dependencies. See
[the OpenDAL reference](../../reference/opendal-extension.md) and
[ADR-0201](../../decisions/0201-opendal-runs-in-an-autonomous-asp-extension.md).

The extension's test project belongs to the required Integration lane and coverage
shard B. Management API tests exercise real product resolution, AEP contributions,
protected configuration, MCP and native filesystem access, including disablement.
The standalone sample and distribution runner verify publication and a process
restart without a running Agentstration product.

Validation on the OpenDAL implementation at `34c8605`:

- Root restore and Release build passed with zero warnings/errors.
- Required Fast lane: 999 passed, zero failed/skipped.
- Required Integration lane: 1,107 discovered, 1,095 passed, zero failed and the
  existing 12 optional PostgreSQL/live-provider skips. No OpenDAL case was skipped.
- Published Linux x64 executable passed real MCP write/retry/list/stat/read, then
  read the same persisted version after process restart. Native files and license
  notices were checked in the published distribution.
- Actual Docker remove/recreate passed with a retained content bind mount and
  networking disabled, using an offline image built from the installed .NET 10.0.8
  and host glibc libraries. The official runtime image pull was refused locally.
- Dedicated [native CI](https://github.com/gbaudrit/agentstration/actions/runs/42509181589)
  passed on Windows x64 and Ubuntu 24.04 x64, including the published-process
  restart smoke. Its Linux job also built the checked-in Dockerfile successfully.
- Exact changed-source formatting verification, 199-ADR validation, LikeC4
  validation/generation and the complete production Docusaurus build passed.
- The separately dispatched [general CI](https://github.com/gbaudrit/agentstration/actions/runs/42509308400)
  was still running when this record was written; its success is not implied.

No new Console/Workplace journey was added. #1033/#1065 keep their existing
ownership and UX validation/merge gates. #1039 remains open for review.
