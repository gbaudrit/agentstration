# OCI image packaging

[Packaging milestone #16](https://github.com/gbaudrit/agentstration/milestone/16)
owns common OCI build/publication tooling and independently versioned extension
and Worker images. [Preview environments #15](https://github.com/gbaudrit/agentstration/milestone/15)
consumes those artifacts; deployment selection, infrastructure and Bootstrap
composition remain external deployment concerns.

## Tracked executables

Autonomous extension projects live under `src/extensions/`, retaining their
`Agentstration.Extensions.<Name>` project and assembly names. Product-owned
extension management modules (`Agentstration.Extensions`, `.Aep`, `.Api` and
`.Contracts`) remain directly under `src/`. The MAF AWP Worker also remains outside
the extension directory. Moving a project does not change its extension identity,
version or image name; catalogue paths and source-based launch commands use the
new directory.

| Component | Packaging issue | Tracking label |
| --- | --- | --- |
| Shared catalogue and publisher | [#1289](https://github.com/gbaudrit/agentstration/issues/1289) | — |
| Foundry (first image vertical) | [#1291](https://github.com/gbaudrit/agentstration/issues/1291) | `extension:foundry` |
| OpenAPI | [#1290](https://github.com/gbaudrit/agentstration/issues/1290) | `extension:openapi` |
| OpenDAL | [#1293](https://github.com/gbaudrit/agentstration/issues/1293) | `extension:opendal` |
| Crawl4AI | [#1297](https://github.com/gbaudrit/agentstration/issues/1297) | `extension:crawl4ai` |
| PostgreSQL document provider | [#1294](https://github.com/gbaudrit/agentstration/issues/1294) | `extension:postgresql` |
| Ollama | [#1298](https://github.com/gbaudrit/agentstration/issues/1298) | `extension:ollama` |
| llama.cpp | [#1292](https://github.com/gbaudrit/agentstration/issues/1292) | `extension:llama-cpp` |
| LocalAI | [#1299](https://github.com/gbaudrit/agentstration/issues/1299) | `extension:localai` |
| Git | [#1295](https://github.com/gbaudrit/agentstration/issues/1295) | `extension:git` |
| Utilities | [#1296](https://github.com/gbaudrit/agentstration/issues/1296) | `extension:utilities` |
| Independent MAF AWP Worker | [#1300](https://github.com/gbaudrit/agentstration/issues/1300) | — |
| Authoritative server and local SQLite preview | [#1281](https://github.com/gbaudrit/agentstration/issues/1281) | — |

The PostgreSQL extension provides document storage and is distinct from the
optional server relational storage profile. The MAF Worker is an AWP executable,
not an AEP extension. Each packaging issue has a native dependency on #1289;
these are independent milestone tasks, not sub-issues of the shared tooling task.
The labels also live in `.github/labels.json` for maintenance through the existing
`scripts/github/apply-issue-labels.ps1` command.

## Foundry pilot

The AEP catalogue contains `foundry`, under
`deploy/images/extensions/foundry.json`. The AWP Worker is described separately
below. Foundry’s recipe reuses `Dockerfile.extension`
for Linux amd64. Future entries are added when their actual build/smoke vertical
is delivered; tracking an issue does not imply an image is already published.

The catalogue also includes `applications/server.json`, kind `server`. The server
uses the product's existing version from `Directory.Build.props`, not the independent
extension/Worker `0.1.0` baseline. The root Dockerfile embeds the Bootstrap catalogue
and accepts the same pinned SDK/runtime build inputs. This entry supports private
GHCR previews only; official server tags and Docker Hub releases retain their
existing product workflow. See [ADR-0255](../decisions/0255-sqlite-previews-consume-private-oci-deliveries.md)
and the [local SQLite preview instructions](preview-environments.md).

Foundry's `version.txt` is its own version source. The project assembly,
advertised AEP extension version and image labels use that version. The initial
value is `0.1.0`, replacing the previous development identity `1.0.0`. Each
extension starts its independently packaged release cycle at `0.1.0` and then
evolves independently; an official image release is not implied by this baseline.
AEP protocol and contribution versions remain separate contracts. See
[ADR-0252](../decisions/0252-extension-images-have-independent-oci-releases.md).

Inspect the catalogue and run the offline packaging contracts:

```sh
python3 scripts/packaging/images.py describe --extension foundry
python3 -m unittest discover -s scripts/packaging -p 'test_*.py' -v
dotnet test --project tests/Agentstration.ModelProviders.Tests --configuration Release --filter 'FullyQualifiedName~FoundryProviderTests' --minimum-expected-tests 1
```

## Build once and validate

Product **CI** calls the reusable **Validate OCI image** workflow for the
implemented image catalogue entries. Foundry-only inputs select Foundry; Worker/AWP
inputs select the Worker and the server/Worker preview validation; server-specific
inputs select the server. Shared packaging inputs select all three. Worker product
changes retain conservative product validation. The required `build-and-test` check includes its result;
Foundry-only changes use this targeted validation while shared or mixed inputs
retain the transverse checks described in [Testing](testing.md#ci-inventory-and-execution-safeguards).
The reusable workflow has read-only repository access and contains no publication
job. There is one PR image build, not a second independently triggered image run.

The workflow files are `.github/workflows/oci-images.yml` (delivery) and
`.github/workflows/validate-oci-image.yml` (reusable validation). Automation that
dispatched `extension-images.yml` must use `oci-images.yml`; callers of
`validate-extension-image.yml` must use `validate-oci-image.yml`. The existing
`extension` input still accepts either catalogue family. Internal job IDs and
artifact names remain compatible with existing result gates and delivery receipts.

The **OCI images** delivery workflow reuses that same validation for
extension release tags and manual dispatch. A manual dispatch takes an implemented
catalogue ID and defaults to validation only. Opting into `publish_preview`
publishes the tested archive to private GHCR after the build/smoke job succeeds.

The build pins SDK and ASP.NET base images by digest, requires a clean source
checkout, and emits an OCI archive, metadata and `delivery.json`. It includes OCI
source/version/revision labels, SBOM and provenance. The delivery records the
archive checksum and OCI digest. Build outputs are stored under ignored
`artifacts/`; credentials and generated state are excluded from build context.

Skopeo stages that exact archive in a disposable loopback-only registry. The
AEP image smoke checks health, unauthenticated discovery rejection, authenticated AEP
identity/version/contribution, OCI labels and restart using a temporary
SharedKeyFile mounted read-only. It makes no Foundry/model request and contains
no Azure credential.

For every AEP image, the same validation job also builds a disposable authoritative
server from the same source revision, using the generic Dockerfile and the same
pinned SDK/runtime bases. `scripts/packaging/aep-extension.smoke.compose.yml` runs only that
server and the exact staged extension digest. The server uses SQLite, deterministic
AI and the existing Development bootstrap with a newly generated disposable
administrator password. The test uses its own Compose network and exposes ports
only on loopback; no local inference server, Worker, Azure resource or downstream
credential is needed for enrollment.

The Compose service is named `extension`. The smoke derives its expected identity,
version, protocol and contribution from the selected catalogue entry. It generates
the authority's per-extension trust settings from `extensionId`, supplying only the
SharedKeyFile mode and the mounted key path in a temporary environment file. No
provider ID or provider-specific endpoint is embedded in the scenario. The real
image validation currently exercises Foundry, the implemented AEP catalogue entry;
offline regression cases also cover other extension identities. A new AEP image
uses this same enrollment scenario when its catalogue/build vertical is added.

The enrollment smoke verifies:

- Health/readiness and protected AEP identity, including rejection of an invalid bearer.
- A real extension-initiated SharedKeyFile announcement, automatic Instance assignment,
  and an available persisted registration with the expected version and endpoint.
- Rejection of an announcement signed with another key, including for an existing instance.
- Idempotent signed reannouncement and preservation of enrollment/registration UIDs,
  generations and assignment after **both containers are removed and recreated**.

The generated key is mounted read-only into both processes. Extension instance
state and server data are separate writable mounts; the extension receives no
server database mount. The smoke observes only technical enrollment/registration
records through a read-only SQLite connection. Temporary keys, passwords and
stores are removed during cleanup; container logs/configuration and database
payloads are not uploaded. `enrollment-smoke.json` records the extension digest,
source SHA, version, local authority image ID and tested identity checks beside
`delivery.json`. This is a tested source/digest combination, not a claim of
compatibility with every Agentstration version.

To repeat the check after staging an archive, build the server fixture with
`Dockerfile.extension`, `PROJECT=src/Agentstration.Web/Agentstration.Web.csproj`,
`ENTRYPOINT_DLL=Agentstration.Web.dll`, and the `SDK_IMAGE`/`RUNTIME_IMAGE` values
from `delivery.json.buildImages`. Label it with
`org.opencontainers.image.revision=<delivery sourceSha>`. The checked-out source
and bootstrap catalog must match that revision. The reusable workflow contains
the exact build command. Then run:

```sh
python3 scripts/packaging/enrollment_smoke.py \
  --delivery artifacts/extension/delivery.json \
  --image localhost:5000/extension@sha256:<delivery-digest> \
  --authority-image agentstration-packaging-smoke-authority:local \
  --output artifacts/extension/enrollment-smoke.json
```

A Linux Docker host and Compose v2 are required. Both containers run with the
invoking user/group so mounted test data remains removable. Only a locally present server fixture is accepted;
this server image is never published by extension packaging. The scenario is a
packaging test fixture, not the preview topology tracked by #1281. Live inference
and creation of Model Providers remain separate opt-in validation.

Local build from a committed clean checkout:

```sh
python3 scripts/packaging/images.py build --extension foundry \
  --repository gbaudrit/agentstration --directory artifacts/extension
```

Requirements: Docker Buildx with OCI archive/attestation support, Python 3.11+,
and network access to build dependencies. Skopeo is needed for staging/publication.
The archive and delivery manifest must be retained together. CI retains them for
seven days; preserve release artifacts elsewhere before expiry if later promotion
is required.

## Private preview and official release

The default private destination is
`ghcr.io/gbaudrit/agentstration-extension-foundry:sha-<full-source-sha>`.
The publisher checks existing GHCR package visibility before copying, verifies
private visibility after publication, and uses the job-scoped `GITHUB_TOKEN`.
The package must already exist with explicitly verified private visibility and
grant this repository Actions access. Provision it through the registry owner's
administration process before enabling preview publication. Missing packages,
inaccessible metadata and non-private visibility stop publication before any
image copy; the publisher never relies on automatic package creation or registry
visibility defaults. No broad registry credential enters the build.

### First package initialization

GitHub creates a GHCR package on its first push; the Packages page does not offer
creation of an empty package. Preparing a private destination is therefore a
separate registry initialization operation, not a prerequisite that can be met
by creating an empty repository through that page.

Before sending an application image, an administrator can use an empty directory
containing only a `FROM scratch` Dockerfile to publish a reserved `registry-init-*`
image. This placeholder must contain no application code, configuration, model
or credential. Verify the resulting package visibility through the authenticated
Packages API and the owner interface. If linking the package to a repository,
verify visibility again with a second empty image carrying the same source label
as the application. Grant the Agentstration repository Actions access only to the
verified private destination.

In the package settings, open **Manage Actions access**, choose **Add repository**,
select `gbaudrit/agentstration` and grant **Write**. Connecting the package to a
repository is distinct from granting this Actions role. Verify that the normal
repository `GITHUB_TOKEN` can read the private package before publishing; an owner
token successfully reading it does not establish repository Actions access.

If either check reports public/internal visibility or metadata cannot be read,
stop before the application upload and remove only the just-created placeholder
content. Do not assume repository access inheritance implies private visibility.
Investigate the registry/owner creation behavior instead of repeatedly publishing
the real image. Registry-owner access may be needed for this one-time operation;
normal publication continues to use the job-scoped `GITHUB_TOKEN`.

An official Foundry release uses Git tag `extension/foundry/v0.1.0`. The workflow
checks that the tag matches `version.txt` and belongs to `main`. It then builds
and validates once, publishes the same OCI archive to private GHCR with the
extension version tag, and copies it to
`docker.io/agentstration/extension-foundry:0.1.0` without rebuilding.

Docker Hub publication reuses repository secrets `DOCKERHUB_USERNAME` and
`DOCKERHUB_TOKEN`. Optional repository variable `DOCKERHUB_NAMESPACE` overrides
the default `agentstration` namespace. The target Docker Hub repository must be
configured public for official distribution; this workflow does not change
Docker Hub repository visibility. Release publication occurs only from matching
extension tags, not PRs or an ordinary manual preview dispatch.

Copying uses Skopeo `--all --preserve-digests` to retain the OCI index and
attestations. The destination must support those media types; a registry that
cannot preserve the artifact fails publication. The publisher validates the
archive checksum and refuses replacement of a tag with a different digest.
Deployment consumers must retain the actual digest reference, not resolve a
mutable convenience tag at deployment time.

## MAF AWP Worker

[#1300](https://github.com/gbaudrit/agentstration/issues/1300) adds `maf-worker` under
`deploy/images/workers/`. It is an **AWP Worker**, not an AEP extension. Its existing
`Dockerfile.worker` accepts the same pinned base-image inputs and uses a direct
`dotnet` entrypoint. The independent initial version is `0.1.0`; AWP `1.0`, workload
authentication `1`, capability `microsoft-agent-framework/1.0`, and execution
material versions `1.0`/`1.1` remain separate. See [ADR-0254](../decisions/0254-maf-worker-images-have-independent-awp-releases.md).

```sh
python3 scripts/packaging/images.py describe --image maf-worker
python3 scripts/packaging/images.py build --image maf-worker \
  --repository gbaudrit/agentstration --directory artifacts/worker
```

The historical `--extension` spelling and workflow dispatch input `extension`
remain supported for compatibility; they select a catalogue image ID. The manual
**OCI images** selector offers `foundry` and `maf-worker` and defaults to validation
without publication. Private Worker previews target
`ghcr.io/gbaudrit/agentstration-worker-maf:sha-<full-source-sha>`. Establish private
package visibility and repository Actions Write access before opting into
`publish_preview`; missing/inaccessible/public metadata blocks the upload.

An official tag would be `worker/maf-worker/v0.1.0` and would copy the same archive
to `docker.io/agentstration/worker-maf:0.1.0`. **Docker Hub and official tags remain
deferred**; do not create this tag merely to validate a private preview. Image
validation is not evidence of an actual registry publication.

New Worker delivery documents contain `component` metadata; existing AEP receipts
retain `extension`. The shared publisher reads either format and rejects ambiguous
documents. Per-image OCI artifacts are named `oci-image-<id>-<run>-<attempt>`;
small delivery/smoke evidence is retained separately as
`oci-evidence-<id>-<run>-<attempt>` for 30 days. Archives remain retained seven days.

### Worker configuration and lifecycle

Supply `Agentstration:RuntimeWorker:AuthorityUrl`, stable `WorkerId`, `CredentialId`,
`InstanceId` and `SharedKeyFile`. The authority configures the same IDs and file
under `Agentstration:AwpWorkerTrust:Credentials`. Keep the key outside images and
mount it read-only. Distinct installations need distinct Worker and credential
IDs/files. HTTP requires explicit `AllowInsecureHttp=true` for isolated Development;
use HTTPS outside that fixture. Never supply a database connection or server data
mount to the Worker.

Alternatively, the existing outbound pairing path uses `CredentialStateFile` and
a one-time code through standard input or protected `PairingCodeFile`. Persist the
credential-state directory outside the container, restrict access, and remove the
pairing file after use. Do not place a code or credential in command-line arguments,
images or logs. Pairing is not AEP enrollment.

`MaximumConcurrentAssignments` bounds whole Runs (default 1, range 1–64);
`MaximumConcurrentProcessRuns` bounds process units (default 4, range 1–16).
`/health/live` reports process liveness independently from registration;
`/health/ready` and the existing aggregate `/health` require successful AWP
registration. A restarted process keeps its Worker identity/credential and creates
a fresh session. Restart does not resume active assignments; ADR-0168's explicit
Worker-loss behavior remains authoritative.

### Worker image validation

`scripts/packaging/maf-worker.smoke.compose.yml` uses only the exact Worker digest
and a locally built SQLite authority from the same source/pinned bases. It reuses
the existing Development and deterministic echo Flow bootstrap profiles, with a
random temporary administrator password. Published ports bind only to loopback.
The Worker has only a read-only key mount; server data is a separate mount.

The smoke starts the Worker while the authority is absent and requires live=200,
ready=503. It then authenticates an administrator, verifies that unsigned AWP
registration is rejected, submits a Flow that remains pending without a Worker,
and checks the exact echo output and the assignment's Worker/session. After
removing and recreating both containers, it repeats execution with the same Worker
and credential, requiring a new session. No model or Azure credential is used.

After staging an archive and building the same local authority fixture as the
reusable workflow, run on a Linux Docker host with Compose v2:

```sh
python3 scripts/packaging/worker_smoke.py \
  --delivery artifacts/worker/delivery.json \
  --image localhost:5000/extension@sha256:<delivery-digest> \
  --authority-image agentstration-packaging-smoke-authority:local \
  --output artifacts/worker/worker-smoke.json
```

`worker-smoke.json` records the source, Worker digest/version, authority image ID,
Worker/credential identifiers, sessions and assignment identifiers. Keys, passwords,
full logs and generated databases are not retained. This proves the recorded
source/digest combination, not compatibility with all server versions or live
inference. The fixture is not the preview composition tracked by #1281.

## Boundaries and remaining validation

Selecting this image does not provision Foundry, start a local model or create a
Model Provider. Existing AEP enrollment and product-owned value bindings govern
endpoints, credentials and identity. The existing `deploy/compose/foundry.yml`
remains a source-build development overlay; preview digest-based selection is
tracked separately. Its old Workspace-scoped Bootstrap example must be reconciled
with Instance-only Model Providers before unattended preview reuse.

Registry publication and full image smoke require a successful workflow run.
The initial Foundry `0.1.0` private delivery was published from source
`d322a0a83d3bad7952f3bdd89b3c8c767b5ec904` with OCI index digest
`sha256:501fb133896951312433d1fe370e0d744a5300ec22629edcb59132df3430abab`.
[Publication](https://github.com/gbaudrit/agentstration/actions/runs/45218954877)
and [authenticated pull/discovery/restart verification](https://github.com/gbaudrit/agentstration/actions/runs/45219616968/job/137850022403)
used repository Actions tokens; anonymous pull was denied. That original check
did not include authority enrollment; subsequent deliveries add the Compose gate
above. Linux amd64 is the currently supported image platform.

Docker Hub publication remains deferred by maintainer decision. Do not create an
`extension/foundry/v0.1.0` release tag to validate private previews: that tag also
requests Docker Hub publication. Keep #1291 open for its deferred public delivery
criteria; the pilot does not claim a complete compatibility matrix or a published
Docker Hub release. No extension tag or image is published merely by adding the catalogue.

## PostgreSQL document-storage extension

[#1294](https://github.com/gbaudrit/agentstration/issues/1294) packages the autonomous
PostgreSQL JSONB document-storage extension as catalogue ID `postgresql`, using
`Dockerfile.extension` and the shared OCI workflows. This is separate from the
server's optional PostgreSQL storage profile; SQLite remains the product default.
The source `version.txt`, assembly informational version, advertised AEP identity
and OCI version label share independent version `0.1.0`. AEP `agentstration.io/v1`,
Storage Type `postgresql.jsonb@1.0.0`, Connected Service Type `postgresql@1.0.0`
and the `postgresql-jsonb` ASC provider version `1.0.0` retain their existing versions.
The manifest also advertises the `documents` MCP server at `/mcp`. Its ASC mapping
uses contract `agentstration.storage.contracts.mcp.v1`, revision `1.0`, MCP version
`2026-07-28` and document profile `asc.documents/1`, following
[ADR-0264](../decisions/0264-storage-contract-identity-and-mcp-exposure.md).
These identities remain independent of the extension package version.

```sh
python3 scripts/packaging/images.py describe --extension postgresql
python3 scripts/packaging/images.py build --extension postgresql \
  --repository gbaudrit/agentstration --directory artifacts/postgresql
```

The manual **OCI images** workflow offers `postgresql`. Run it on the reviewed
branch with `publish_preview=true` only after verifying that
`ghcr.io/gbaudrit/agentstration-extension-postgresql` is **private** and that
`gbaudrit/agentstration` has **Manage Actions access → Write**. Missing or unreadable
package metadata stops publication. Follow **First package initialization** above
if an administrator has not provisioned the private destination. Owner-token access
alone does not prove repository Actions access. Retain `delivery.json`, provenance,
SBOM and `publication-ghcr.json`; deployment references must use
`ghcr.io/gbaudrit/agentstration-extension-postgresql@sha256:<recorded-digest>`.
The preview tag is `sha-<full-source-sha>`.

### PostgreSQL extension configuration

Environment variables use .NET's `__` separator:

| Variable | Behavior |
|---|---|
| `Aep__EnrollmentMode=SharedKeyFile` | Protected AEP/ASC/MCP transport using the existing enrollment mechanism. |
| `Aep__SharedKeyFile__Path` | Read-only protected key mount, shared with the authority's per-extension trust configuration. |
| `Aep__SharedKeyFile__ClientId` | Defaults to `agentstration-development`; match it in the explicit caller allowlist. |
| `Aep__SharedKeyFile__AuthorityUrl`, `PublicEndpoint` | Authority announcement URL and reachable extension URL. Use full `Aep__SharedKeyFile__...` names. |
| `Aep__SharedKeyFile__StateFile` | Persist the parent directory read/write to preserve the installation identity across container recreation. |
| `Aep__SharedKeyFile__AllowInsecureHttp` | Explicit isolated Development HTTP opt-in; use HTTPS for ordinary deployment. |
| `PostgreSqlDocuments__AllowedClients__0` (and subsequent indices) | Required explicit allowed authenticated caller identities, maximum 32. The packaging fixture uses the default SharedKeyFile client identity. |
| `PostgreSqlDocuments__AllowPairedClient` | Optional paired-client authorization; requires `Aep__EnrollmentMode=PairingCode`. |
| `PostgreSqlDocuments__ProvisionSchema` | Defaults to `false`; schema creation is an explicit operator action. |
| `PostgreSqlDocuments__AllowLoopbackWithoutTls` | Defaults to `false`; allows non-TLS only for explicitly loopback-bound local development. |
| `PostgreSqlDocuments__Enabled` | Defaults to `true`; disabling denies document callers. |

Do not configure a database connection string in the image or catalogue. Host,
port, database, username and secured password come from the existing typed
Connected Service exports and AEP bound values/Secret capabilities. No database
connection is needed for startup, health, contribution discovery or MCP tool listing.
Database data belongs in separately managed PostgreSQL storage; the extension
needs only its persisted AEP state and read-only key, with permissions restricted
to its process user. Never mount server SQLite databases into the extension.
Preserve the existing 100,000-byte HTTP limit, bounded document/payload policies,
10-second database timeouts, four-connection pools and default VerifyFull TLS.

### PostgreSQL validation and qualification

The shared generic smokes check exact digest/version/source labels, authenticated
AEP discovery of both Storage and Connected Service Type identities, invalid bearer rejection,
SharedKeyFile enrollment, invalid announcement proof rejection, idempotent
reannouncement and preservation of installation/registration identity after both
containers are recreated. The authority fixture uses the same source, pinned
base images, SQLite and deterministic AI. These checks require no PostgreSQL
server or LLM. `PackagingTests` additionally starts the actual extension host and
lists its ASC MCP tools without a database. The catalogue selects the whole
`Agentstration.Extensions.PostgreSql.Tests` offline project; real PostgreSQL rows
are explicitly inconclusive unless `AGENTSTRATION_TEST_ASC_POSTGRES` is supplied.

The existing **Structured documents conformance** workflow remains the real local
PostgreSQL 17 JSONB provider validation, including AEP/MCP/product-boundary tests.
Its database is a disposable isolated CI fixture, separate from server-profile
qualification. See [structured documents](../reference/structured-documents.md)
for document semantics and provisioning permissions. Linux amd64 is the supported
image platform. A successful workflow's delivery/enrollment receipts identify the
exact tested source, extension digest, AEP version and same-source authority image;
there is no claim of a broader product-version compatibility matrix.

Real registry publication and digest smoke results must be taken from completed
workflow evidence; catalogue contracts or source-host tests alone do not establish
private qualification. Keep #1294 open until the private qualification is recorded.
Docker Hub and official release qualification are deferred to
[#1384](https://github.com/gbaudrit/agentstration/issues/1384), retaining same-archive
promotion, independent tag/version checks, immutable source/digest references,
provenance/SBOM, anonymous public pull and health/discovery/enrollment/restart
criteria. Do not create `extension/postgresql/v0.1.0`: the current release path
also requests Docker Hub publication. Preview extension selection is a separate
workstream.
