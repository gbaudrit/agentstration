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
dotnet test --project tests/Agentstration.Extensions.Foundry.Tests --configuration Release --filter 'FullyQualifiedName!~FoundryLiveTests' --minimum-expected-tests 1
```

## Build once and validate

Product **CI** calls the reusable **Validate OCI image** workflow for the
implemented catalogue entries. A single catalogue-driven selector under
`scripts/ci/extensions/` discovers AEP extension ownership; the central selector
combines its result with server/Worker and product inputs. Isolated implemented
extension inputs select only their image, while common packaging and global
.NET/CI inputs retain their appropriate shared checks. The required
`build-and-test` gate waits for the selected validations. See the
[selection matrix](testing.md#catalogue-driven-extension-selection).

The default source directory comes from `project`. A dedicated test project
named `<extension project>.Tests` owns its entire directory, including its project
file. Foundry now uses `Agentstration.Extensions.Foundry.Tests` and owns that
directory. Its offline filter includes Provider, Chat and Streaming tests; live
checks remain explicit opt-ins. See the [isolation inventory](extension-test-isolation.md)
for the other packaging PRs' project/filter handoff.

Shared test projects still require explicit file ownership, for example:

```json
"ci": {
  "testPaths": [
    "tests/SharedProviders.Tests/AlphaTests.cs",
    "tests/SharedProviders.Tests/AlphaLiveTests.cs"
  ]
}
```

Files declared in `ci.testPaths` select their explicit owners rather than every
extension using the same test project. A file may have multiple declared owners;
explicit `ci.sharedPaths` dependencies are still included. Unlisted shared
fixtures and the shared test project file continue to select product checks and
every dependent image. `versionFile`, the catalogue itself,
`dockerfile` and its dedicated recipe directory are also recognized. The
conventional `scripts/packaging/test_<id>_image.py` contract uses underscores for
hyphens in the catalogue ID.

Optional `ci.additionalPaths` declares extension-owned packaging assets, for
example `deploy/<id>/**`, `scripts/packaging/<id>_smoke.py` or a dedicated workflow
whose filename includes the ID. Optional `ci.sharedPaths` selects the dependent
image for shared SDK/protocol inputs **without** granting product exemptions.
All paths are repository-relative; test patterns must stay inside the declared
test project. This metadata does not change extension, protocol or contribution
versions, and introduces no executable catalogue entry by itself.

Only delivered catalogue entries are recognized. Unknown or removed ownership
remains conservative, and malformed entries stop classification. The offline
contracts create temporary catalogues to exercise all pending extension shapes;
new packaging PRs can add their real catalogue without editing the central
classifier. Existing packaging PRs must retain the new central selector when
synchronizing after this refactor merges; obsolete provider-specific rules and
central test additions should not be reintroduced. Resource Plan UX's generic
extension exclusion also avoids per-provider workflow edits.

The workflow files are `.github/workflows/oci-images.yml` (delivery) and
`.github/workflows/validate-oci-image.yml` (reusable validation). Automation that
dispatched `extension-images.yml` must use `oci-images.yml`; callers of
`validate-extension-image.yml` must use `validate-oci-image.yml`. The existing
`extension` input still accepts either catalogue family. Internal job IDs and
artifact names remain compatible with existing result gates and delivery receipts.

The **OCI images** delivery workflow reuses that same validation for
extension release tags and manual dispatch. A manual dispatch takes an implemented
catalogue ID as a string and defaults to validation only; its existing catalogue
validator rejects unknown IDs before any build or publication. Opting into `publish_preview`
publishes the tested archive to private GHCR after the build/smoke job succeeds.

Automatic PR previews instead promote an existing successful PR CI archive.
Their `preview_pr`, `source_sha`, `ci_run_id` and `ci_run_attempt` inputs bind
publication to current opt-in and exact CI provenance; this mode always skips
the build job. PR OCI validation uses the head SHA while product functional
checks retain GitHub's merge commit. See [selective preview images](preview-automation.md#selective-images).

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
image validation exercises implemented AEP catalogue entries, including OpenAPI;
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

## OpenAPI extension

The `openapi` catalogue entry packages the autonomous
`Agentstration.Extensions.OpenApi` host with `Dockerfile.extension`. Its independent
version starts at `0.1.0`; `version.txt` feeds the assembly, advertised AEP identity
and OCI version label. AEP remains `agentstration.io/v1`; schema format
`openapi.validation`, connected-service contracts and profile bundles retain their
existing contribution versions (`1.0.0`, with the embedded discovery Flow at
`1.0.2`). These versions have separate compatibility meanings.

The image advertises schema validation, `openapi.contract.discover` and
`openapi.operation.invoke`, the `openapi-governed` and `openapi-connected-services`
profile bundles, connected-service type `openapi`, operation type
`openapi-operation`, and the existing `/mcp` server. The generic protected AEP
smoke checks all catalogue contribution families; the selected offline MSTest
project also checks MCP tools, contract discovery/invocation and manifest validity.
It uses deterministic `HttpMessageHandler` fixtures, synthetic `.example.test`
destinations and a fake DNS resolver; no external API or downstream credential is
required. The existing tests cover domain/address/port restrictions, explicit
same-origin candidates, exact contract digests, reference rules, payload/media
bounds and cancellation. Packaging does not relax those policies.

```sh
python3 scripts/packaging/images.py describe --extension openapi
python3 scripts/packaging/images.py build --extension openapi \
  --repository gbaudrit/agentstration --directory artifacts/openapi
# After committing a clean source revision, through the trusted delivery workflow:
gh workflow run oci-images.yml --ref <branch-or-main> \
  -f extension=openapi -f publish_preview=true
```

Before that publication dispatch, an administrator must initialize
`ghcr.io/gbaudrit/agentstration-extension-openapi` as **private** and verify
**Manage Actions access → gbaudrit/agentstration → Write**, following
[First package initialization](#first-package-initialization). Package linkage alone
is insufficient. If metadata or access cannot be verified, finish code/tests/PR
and stop before uploading the application image. The publisher verifies privacy
again and copies the validated archive without rebuilding it. Retain
`delivery.json`, the OCI archive (including provenance/SBOM),
`enrollment-smoke.json` and `publication-ghcr.json`: together they identify the
source SHA, `0.1.0` version, exact digest, pinned build bases and tested SQLite
authority image. Use the receipt's `ghcr.io/...@sha256:...` reference for deployment.

Required and optional runtime settings use standard .NET environment syntax:

| Setting | Behavior |
| --- | --- |
| `OpenApi__AllowedDomains__0` (and subsequent indexes) | Required explicit destination allowlist; no default external destination. |
| `OpenApi__AllowedPorts__0` (and subsequent indexes) | Defaults to `80`, `443`. |
| `OpenApi__AllowPrivateAddresses` | Defaults to `false`; loopback/link-local remain denied. |
| `OpenApi__MaximumCandidatePaths` / `MaximumOperations` | Defaults to `8` / `1000`. |
| `OpenApi__MaximumDocumentBytes` / `MaximumResponseBytes` | Defaults to 2 MiB / 4 MiB. |
| `OpenApi__MaximumReferences` / `MaximumReferenceDepth` | Defaults to `256` / `16`. |
| `OpenApi__MaximumPages` / `TimeoutSeconds` | Defaults to `10` / `30`. |
| `OpenApi__AllowedMethods__<index>`, `AllowedContractMediaTypes__<index>`, `AllowedResponseMediaTypes__<index>` | Preserve the explicit method and media-type policies from `OpenApiOptions`; override only for intended destinations. |
| `Aep__EnrollmentMode` | `SharedKeyFile` for the authenticated container path. |
| `Aep__SharedKeyFile__Path` | Read-only mounted key file, shared only with the authority. |
| `Aep__SharedKeyFile__AuthorityUrl` / `PublicEndpoint` | Authority and extension URLs reachable from their respective hosts. |
| `Aep__SharedKeyFile__StateFile` | Writable persisted instance file, for example `/var/lib/aep/instance`. |
| `Aep__SharedKeyFile__AllowInsecureHttp` | Explicit `true` only for the isolated Development HTTP fixture; use HTTPS otherwise. |

Mount the protected key read-only at `/run/secrets/aep-key` and a dedicated writable
state directory at `/var/lib/aep`; preserve the instance file across container
recreation. Match host ownership/permissions to the container user. No server
database, downstream credential, contract payload or generated state belongs in
the image. Prefer the existing product-managed protected value/Secret bindings
for API credentials; do not place credential values in Docker arguments, catalogue
settings, logs or artifacts. The catalogue's `smokeEnvironment` contains only the
public `fixture.example.test` startup allowlist and does not enable private access
or perform an outbound request. Both generic smokes consume those public settings.

Linux amd64 is the supported image platform. A delivery qualifies only the source,
extension digest, same-source SQLite authority and AEP combination recorded by its
successful smoke evidence; no private publication, live API compatibility or other
platform is implied by a catalogue entry. Generic smokes cover health, authenticated
discovery, invalid bearer/signature rejection, SharedKeyFile enrollment, recreation
and persisted identities. The offline OpenAPI tests cover provider behavior with
HTTP fixtures. The catalogue ID `openapi` also derives the selectable label
`preview:extension:openapi` through the #1389 mechanism. After this catalogue entry
reaches `main`, `.github/workflows/preview-extension-labels.yml` synchronizes that
label using `scripts/github/apply-issue-labels.ps1 -PreviewExtensionsOnly`.
The label selects the extension only when applied to a preview request through
the existing preview workflow; packaging does not assign it or trigger deployment.
A dry run with `-DryRun -PreviewExtensionsOnly` verifies the derived name without
changing repository labels.

Related to #1290; keep it open until private qualification is recorded. Docker Hub
is deferred to #1384, preserving official release tag/version and trusted-main
checks, same-archive promotion with provenance/SBOM and per-registry digest receipts,
anonymous public pull plus health/discovery/enrollment/restart qualification,
credential administration, retry/partial-publication and immutable-version conflict
criteria. Do not create `extension/openapi/v0.1.0`: the current tag path also requests
Docker Hub publication.

## Ollama AEP extension

[#1298](https://github.com/gbaudrit/agentstration/issues/1298) adds the implemented
`deploy/images/extensions/ollama.json` entry and the manual **OCI images** identifier
`ollama`. It packages only `Agentstration.Extensions.Ollama`, using the same
`Dockerfile.extension`, builder, publisher and generic AEP smokes as Foundry.
No Ollama inference server, model file or model acquisition command is included.
The existing `deploy/compose/ollama.yml` remains a separate source-build topology.
The trusted-main preview-label synchronization from
[#1389](https://github.com/gbaudrit/agentstration/pull/1389) derives
`preview:extension:ollama` from this catalogue ID once the entry reaches `main`.
It does not deploy anything or assign the label to issues. Preview deployment
selection remains separate from packaging and requires a qualified private OCI
delivery; see [preview on demand](preview-on-demand.md).

Its own `version.txt` starts at `0.1.0` and feeds the assembly informational
version, AEP identity `Agentstration.Extensions.Ollama` and OCI version label.
The AEP protocol stays `agentstration.io/v1`; the model-provider contribution is
`ollama`, and `io.agentstration.ollama/model-profile` stays at option version
`1.0.0`. Packaging does not migrate persisted option contracts or product versions.
Linux amd64 is the supported image platform.

```sh
python3 scripts/packaging/images.py describe --extension ollama
python3 scripts/packaging/images.py build --extension ollama \
  --repository gbaudrit/agentstration --directory artifacts/ollama
dotnet test --project tests/Agentstration.Extensions.Ollama.Tests --configuration Release \
  --filter 'FullyQualifiedName~Agentstration.ModelProviders.Tests' --minimum-expected-tests 1
```

Ollama-only source, catalogue and dedicated `Agentstration.Extensions.Ollama.Tests`
changes select its targeted OCI validation. Changes to
`tests/OllamaInferenceTestFixture.cs`, shared with product tests, select Ollama
and retain transverse product validation. Shared AEP/enrollment fixtures select every implemented AEP image;
shared packaging selects all implemented image families. Mixed product changes,
shared test projects, SDK/package inputs and classifier changes retain transverse
validation. Preserve the other implemented entries when integrating concurrent
packaging changes into the selector and classifier.

### Ollama container settings and state

| Variable | Purpose and default |
| --- | --- |
| `ASPNETCORE_URLS` | Container listener, `http://+:8080` from the shared recipe. Publish it only to the intended private network. |
| `Ollama__Endpoint` | Native inference HTTP endpoint; defaults to `http://localhost:11434`. A container normally needs the explicit DNS name of a separately operated server. Health/discovery/enrollment do not require that server. |
| `Ollama__Inference__Timeout` | Finite chat and streaming budget, default `00:01:30`; accepted range `00:00:00.001`–`1.00:00:00`. Model listing retains the existing 90-second client budget. |
| `Aep__EnrollmentMode` | Use `SharedKeyFile` for unattended private delivery. The executable development default is `Disabled`; do not expose that mode. |
| `Aep__SharedKeyFile__Path` | Read-only file containing the enrollment/invocation key; required for SharedKeyFile. |
| `Aep__SharedKeyFile__ClientId` | Technical caller identity, default `agentstration-development`; configure it for the installation. |
| `Aep__SharedKeyFile__AuthorityUrl` | Authoritative Agentstration server URL; enables signed announcements. Use HTTPS outside isolated Development fixtures. |
| `Aep__SharedKeyFile__PublicEndpoint` | Extension URL reachable from that authority, required when announcing. |
| `Aep__SharedKeyFile__StateFile` | Persisted installation identity, default `/app/.aep/shared-key-instance`. Prefer an explicit file in a dedicated writable state mount. |
| `Aep__SharedKeyFile__AllowInsecureHttp` | Default `false`; explicitly `true` only for the isolated Development smoke network. |

Mount the key read-only in both the extension and authority with owner-only file
permissions. The authority configures matching trust under
`Agentstration__Extensions__Agentstration.Extensions.Ollama__EnrollmentMode`
and `Agentstration__Extensions__Agentstration.Extensions.Ollama__SharedKeyFile__Path`. Give the extension only its dedicated writable instance-state mount,
owned by its configured container UID/GID; retain it when recreating containers.
It needs no server database, model volume or inference-server state mount. Keep
keys, model files and generated state outside the build context and artifacts.

### Ollama qualification and private delivery

`OllamaPackagingTests` checks public health, rejected anonymous/invalid discovery,
authenticated identity and independent assembly/option versions. Its in-process
HTTP fixture returns fixed native version, model capabilities and chat results;
unexpected routes fail, so no model download or real LLM is involved. Existing
mapping and native streaming-timeout tests run alongside packaging tests in the
dedicated extension project. Product reasoning and two-hop timeout tests remain
in their product test project, outside this catalogue's offline filter.
The Docker gates reuse generic health/discovery/restart and SQLite-authority
SharedKeyFile enrollment, invalid-proof rejection and persistent identity checks.
The authority and extension use the exact same source and pinned build bases.

The compatibility baseline is the selected source revision with its local SQLite,
deterministic Agentstration authority and AEP `agentstration.io/v1`. The hosted
native fixture does not establish compatibility with every Ollama server/model.
`delivery.json` supplies source SHA, extension version, OCI digest, pinned bases,
archive checksum, provenance and SBOM; `enrollment-smoke.json` supplies the tested
source/digest/authority identity combination. Validation artifacts and publication
receipts are the evidence of checks actually completed; adding this catalogue is
not evidence of registry publication.

Before publishing, an administrator must establish
`ghcr.io/gbaudrit/agentstration-extension-ollama` as **private** and grant
`gbaudrit/agentstration` **Manage Actions access → Write**, following the first
package initialization procedure above. An owner token reading metadata alone
does not prove repository Actions access. Use the existing `oci-images.yml`
workflow with `extension=ollama` and `publish_preview=true` only after both checks.
The job's `GITHUB_TOKEN` publishes the validated archive under
`sha-<full-source-sha>` and retains the immutable digest receipt. Missing,
inaccessible or non-private package metadata stops the shared publisher before
upload. No registry credential is passed into builds or extension settings.

Keep #1298 open until effective private qualification is recorded. Docker Hub is
explicitly deferred to [#1384](https://github.com/gbaudrit/agentstration/issues/1384):
release-tag/main-ancestry validation, same-archive promotion without rebuilding,
public anonymous pull and lifecycle smoke, immutable digest/provenance/SBOM
consistency, release-only credentials and retry/conflict handling remain required
for future public qualification. Do not create `extension/ollama/v0.1.0`; the
current tag path also invokes Docker Hub.

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

## OpenDal private image

Related to [#1293](https://github.com/gbaudrit/agentstration/issues/1293).
`deploy/images/extensions/opendal.json` is an implemented AEP catalogue entry with
independent version `0.1.0`. Its version file drives the project assembly, protected
AEP discovery identity and OCI metadata. AEP protocol and contribution versions
remain independent.

Reuse `deploy/opendal/Dockerfile` and the existing **OpenDAL native packaging**
workflow; there is no second native publication pipeline. Its SDK/runtime inputs
are digest-resolved by the shared builder; the Linux runtime retains the Noble
glibc baseline (`mcr.microsoft.com/dotnet/aspnet:10.0.8-noble`) and `linux-x64` RID.
The image preserves Apache.OpenDAL 0.3.1 native/managed assets and the Apache,
MPL-2.0 and CC0 license/NOTICE files. Alpine/musl is unsupported. Windows native
qualification remains in the existing native workflow, independently from this
Linux amd64 OCI catalogue.

The real AEP identity is lowercase `agentstration.extensions.opendal`. The independent
extension version `0.1.0` does not change `opendal.fs`, `opendal.s3`, `opendal.azblob`
contribution/provider versions `1.0.0`, ASC identity/revision or MCP exposure.
Mount writable local data at `/data`, configure `OpenDal__RootDirectory=/data`
and explicit `OpenDal__AllowedClients__0` entries matching enrolled client IDs.
The process UID must own this data; authentication keys are separate read-only
mounts. S3/Azure credentials remain external secured bindings. The generic health smoke runs with the invoking UID/GID, as does the enrollment
Compose fixture: this keeps the host-owned mode-0600 key readable without changing
its permissions or requiring a root process. The image smoke
reuses `samples/OpenDalSmoke` to verify native assets/licenses, real ASC/MCP
write/list/read and persisted content after restart against disposable local
storage. Synthetic loopback S3/Azure peers stay deterministic; no real cloud
storage or credential is needed. Snapshot retention is not added.

### Authentication, state and publication

Use the existing AEP SharedKeyFile settings: `Aep__EnrollmentMode=SharedKeyFile`,
`Aep__SharedKeyFile__Path`, `AuthorityUrl`, `PublicEndpoint`, and `StateFile`.
Persist the extension state file in its own writable directory, owned by the
process UID; mount the per-installation shared key read-only, mode 0600. The
authority has its own database/state mount and the matching per-extension trust.
Do not share server data volumes or put secrets in the image. HTTP requires an
explicit isolated Development allowance; use HTTPS for normal remote traffic.

The generic smokes check liveness, protected discovery, invalid bearer/proof
rejection, real SharedKeyFile enrollment, idempotent reannouncement, restart and
identity retention after recreation of both extension and SQLite authority.
The qualification baseline is Linux amd64, .NET 10, current source/server revision,
SQLite and deterministic fixtures. Actual source/digests, pinned bases and smoke
results are retained in `delivery.json`, `*-smoke.json` and the PR/FR; platform or
live-service combinations without a successful run are not claimed as tested.

Private destination: `ghcr.io/gbaudrit/agentstration-extension-opendal:sha-<full-source-sha>`.
Before application publication, use a package-owner identity with verified
read/write package access to initialize a missing destination only with an empty
`FROM scratch` image and reserved `registry-init-*` tag. A 404 alone does not prove
absence. Verify **Private** before and after repository association, and grant
**Manage Actions access → gbaudrit/agentstration → Write** at
[package settings](https://github.com/users/gbaudrit/packages/container/agentstration-extension-opendal/settings).
The bootstrap secret is confined to this initialization; normal
`oci-images.yml` dispatch uses `extension=opendal`, `publish_preview=true` and
job-scoped `GITHUB_TOKEN`. The publisher verifies privacy before/after copying the
same tested archive with digests and provenance/SBOM attestations preserved.
If package administration is missing, complete code/tests/PR first and record that
remaining step rather than attempting an application push.

Deployment selector label: `preview:extension:opendal`. Reuse
`preview-extension-labels.yml` and `apply-issue-labels.ps1 -PreviewExtensionsOnly`;
the main workflow derives the label after catalogue merge, while the same script
can provision it from this implemented branch beforehand. Preserve work-tracking
`extension:*` labels. Label existence does not publish an image or deploy anything;
deployment requires a privately published, resolvable digest. Do not automatically
assign the label to issues or trigger deployment during packaging qualification.

Docker Hub is deferred to [#1384](https://github.com/gbaudrit/agentstration/issues/1384),
including matching release-version/tag and main ancestry, same-archive promotion,
digest/provenance/SBOM receipts, anonymous pull and lifecycle qualification,
release-only credentials, retries and immutable-tag conflict behavior. Do not
create `extension/opendal/v0.1.0`: that release path also triggers Docker Hub.
Keep #1293 open until effective private qualification is recorded; use no
premature auto-closing keyword.

## LocalAI private image

Related to [#1299](https://github.com/gbaudrit/agentstration/issues/1299).
`deploy/images/extensions/localai.json` is an implemented AEP catalogue entry with
independent version `0.1.0`. Its version file drives the project assembly, protected
AEP discovery identity and OCI metadata. AEP protocol and contribution versions
remain independent.

Set `LocalAI__Endpoint` (default `http://localhost:8081`) to an existing HTTP(S)
LocalAI service. Supply optional `LocalAI__ApiKey` through protected runtime
configuration, never image layers, command arguments, receipts or logs. The image
contains only the AEP extension, with no inference server or model. The separate
`deploy/compose/localai.yml` source-build topology uses `localai/localai:v4.9.0`
and an explicit model volume; its empty model catalogue does not trigger downloads.
Deterministic HTTP fixtures and protected-discovery packaging tests run in the
dedicated `Agentstration.Extensions.LocalAI.Tests` project. The OCI filter excludes
`LocalAiIntegrationTests`, which requires an explicit live LocalAI/model opt-in.
Shared product inference-timeout tests remain outside this extension validation;
packaging smokes do not claim live-provider qualification.

### Authentication, state and publication

Use the existing AEP SharedKeyFile settings: `Aep__EnrollmentMode=SharedKeyFile`,
`Aep__SharedKeyFile__Path`, `AuthorityUrl`, `PublicEndpoint`, and `StateFile`.
Persist the extension state file in its own writable directory, owned by the
process UID; mount the per-installation shared key read-only, mode 0600. The
authority has its own database/state mount and the matching per-extension trust.
Do not share server data volumes or put secrets in the image. HTTP requires an
explicit isolated Development allowance; use HTTPS for normal remote traffic.

The generic smokes check liveness, protected discovery, invalid bearer/proof
rejection, real SharedKeyFile enrollment, idempotent reannouncement, restart and
identity retention after recreation of both extension and SQLite authority.
The qualification baseline is Linux amd64, .NET 10, current source/server revision,
SQLite and deterministic fixtures. Actual source/digests, pinned bases and smoke
results are retained in `delivery.json`, `*-smoke.json` and the PR/FR; platform or
live-service combinations without a successful run are not claimed as tested.

Private destination: `ghcr.io/gbaudrit/agentstration-extension-localai:sha-<full-source-sha>`.
Before application publication, use a package-owner identity with verified
read/write package access to initialize a missing destination only with an empty
`FROM scratch` image and reserved `registry-init-*` tag. A 404 alone does not prove
absence. Verify **Private** before and after repository association, and grant
**Manage Actions access → gbaudrit/agentstration → Write** at
[package settings](https://github.com/users/gbaudrit/packages/container/agentstration-extension-localai/settings).
The bootstrap secret is confined to this initialization; normal
`oci-images.yml` dispatch uses `extension=localai`, `publish_preview=true` and
job-scoped `GITHUB_TOKEN`. The publisher verifies privacy before/after copying the
same tested archive with digests and provenance/SBOM attestations preserved.
If package administration is missing, complete code/tests/PR first and record that
remaining step rather than attempting an application push.

Deployment selector label: `preview:extension:localai`. Reuse
`preview-extension-labels.yml` and `apply-issue-labels.ps1 -PreviewExtensionsOnly`;
the main workflow derives the label after catalogue merge, while the same script
can provision it from this implemented branch beforehand. Preserve work-tracking
`extension:*` labels. Label existence does not publish an image or deploy anything;
deployment requires a privately published, resolvable digest. Do not automatically
assign the label to issues or trigger deployment during packaging qualification.

Docker Hub is deferred to [#1384](https://github.com/gbaudrit/agentstration/issues/1384),
including matching release-version/tag and main ancestry, same-archive promotion,
digest/provenance/SBOM receipts, anonymous pull and lifecycle qualification,
release-only credentials, retries and immutable-tag conflict behavior. Do not
create `extension/localai/v0.1.0`: that release path also triggers Docker Hub.
Keep #1299 open until effective private qualification is recorded; use no
premature auto-closing keyword.

## Crawl4AI private image

Related to [#1297](https://github.com/gbaudrit/agentstration/issues/1297).
`deploy/images/extensions/crawl4ai.json` is an implemented AEP catalogue entry with
independent version `0.1.0`. Its version file drives the project assembly, protected
AEP discovery identity and OCI metadata. AEP protocol and contribution versions
remain independent.

Set `Crawl4AI__Endpoint` to a separately operated Crawl4AI service (default
`http://localhost:11235`). `Crawl4AI__AllowedDomains__0` and subsequent indices are
required; preserve explicit allowed ports, private-address denial, depth/page,
payload, timeout and spool limits. The public smoke-only domain
`fixture.example.test` is injected by the test catalogue, not a production default.
Provide downstream authentication with a protected `Crawl4AI__ApiTokenFile` mount
(or protected runtime configuration); the AEP key is separate. Set
`Crawl4AI__ContentDirectory` to an operator-owned writable volume for retained
acquisition content and preserve the retention/quota policy. `/health` is process
liveness; `/health/ready` deliberately depends on the upstream service and may be
503 when it is absent. No upstream server or model is embedded or downloaded.
External mode uses the existing endpoint. The separate Aspire companion mode uses
`Crawl4AI:Enabled=true`, `Provisioning=Managed` and its pinned `unclecode/crawl4ai:0.9.4`
image (override explicitly); `Provisioning=External` disables provisioning.
Neither companion mode nor live internet acquisition is claimed as tested here;
the existing HTTP/DNS fixtures qualify bounded provider behavior offline.

### Authentication, state and publication

Use the existing AEP SharedKeyFile settings: `Aep__EnrollmentMode=SharedKeyFile`,
`Aep__SharedKeyFile__Path`, `AuthorityUrl`, `PublicEndpoint`, and `StateFile`.
Persist the extension state file in its own writable directory, owned by the
process UID; mount the per-installation shared key read-only, mode 0600. The
authority has its own database/state mount and the matching per-extension trust.
Do not share server data volumes or put secrets in the image. HTTP requires an
explicit isolated Development allowance; use HTTPS for normal remote traffic.

The generic smokes check liveness, protected discovery, invalid bearer/proof
rejection, real SharedKeyFile enrollment, idempotent reannouncement, restart and
identity retention after recreation of both extension and SQLite authority.
The qualification baseline is Linux amd64, .NET 10, current source/server revision,
SQLite and deterministic fixtures. Actual source/digests, pinned bases and smoke
results are retained in `delivery.json`, `*-smoke.json` and the PR/FR; platform or
live-service combinations without a successful run are not claimed as tested.

Private destination: `ghcr.io/gbaudrit/agentstration-extension-crawl4ai:sha-<full-source-sha>`.
Before application publication, use a package-owner identity with verified
read/write package access to initialize a missing destination only with an empty
`FROM scratch` image and reserved `registry-init-*` tag. A 404 alone does not prove
absence. Verify **Private** before and after repository association, and grant
**Manage Actions access → gbaudrit/agentstration → Write** at
[package settings](https://github.com/users/gbaudrit/packages/container/agentstration-extension-crawl4ai/settings).
The bootstrap secret is confined to this initialization; normal
`oci-images.yml` dispatch uses `extension=crawl4ai`, `publish_preview=true` and
job-scoped `GITHUB_TOKEN`. The publisher verifies privacy before/after copying the
same tested archive with digests and provenance/SBOM attestations preserved.
If package administration is missing, complete code/tests/PR first and record that
remaining step rather than attempting an application push.

Deployment selector label: `preview:extension:crawl4ai`. Reuse
`preview-extension-labels.yml` and `apply-issue-labels.ps1 -PreviewExtensionsOnly`;
the main workflow derives the label after catalogue merge, while the same script
can provision it from this implemented branch beforehand. Preserve work-tracking
`extension:*` labels. Label existence does not publish an image or deploy anything;
deployment requires a privately published, resolvable digest. Do not automatically
assign the label to issues or trigger deployment during packaging qualification.

Docker Hub is deferred to [#1384](https://github.com/gbaudrit/agentstration/issues/1384),
including matching release-version/tag and main ancestry, same-archive promotion,
digest/provenance/SBOM receipts, anonymous pull and lifecycle qualification,
release-only credentials, retries and immutable-tag conflict behavior. Do not
create `extension/crawl4ai/v0.1.0`: that release path also triggers Docker Hub.
Keep #1297 open until effective private qualification is recorded; use no
premature auto-closing keyword.

## LlamaCpp private image

Related to [#1292](https://github.com/gbaudrit/agentstration/issues/1292).
`deploy/images/extensions/llama-cpp.json` is an implemented AEP catalogue entry with
independent version `0.1.0`. Its version file drives the project assembly, protected
AEP discovery identity and OCI metadata. AEP protocol and contribution versions
remain independent.

The catalogue ID is `llama-cpp`; its real model-provider contribution is `llamacpp`.
Set `LlamaCpp__Endpoint` (default `http://localhost:8080`) to an existing HTTP(S)
inference endpoint. The extension includes no inference executable or GGUF model.
The existing `deploy/compose/llama-cpp.yml` is a separate source-build companion
topology using `ghcr.io/ggml-org/llama.cpp:server-b10830`; supply the GGUF file and
model volume explicitly. No model is downloaded implicitly. Container packaging
qualification does not qualify that companion or real GGUF inference. The affected
provider and protected-discovery packaging tests run in the dedicated
`Agentstration.Extensions.LlamaCpp.Tests` project with deterministic fixtures.
The OCI filter excludes the opt-in `LlamaCppIntegrationTests`; shared product
inference-timeout tests remain outside this extension validation.

### Authentication, state and publication

Use the existing AEP SharedKeyFile settings: `Aep__EnrollmentMode=SharedKeyFile`,
`Aep__SharedKeyFile__Path`, `AuthorityUrl`, `PublicEndpoint`, and `StateFile`.
Persist the extension state file in its own writable directory, owned by the
process UID; mount the per-installation shared key read-only, mode 0600. The
authority has its own database/state mount and the matching per-extension trust.
Do not share server data volumes or put secrets in the image. HTTP requires an
explicit isolated Development allowance; use HTTPS for normal remote traffic.

The generic smokes check liveness, protected discovery, invalid bearer/proof
rejection, real SharedKeyFile enrollment, idempotent reannouncement, restart and
identity retention after recreation of both extension and SQLite authority.
The qualification baseline is Linux amd64, .NET 10, current source/server revision,
SQLite and deterministic fixtures. Actual source/digests, pinned bases and smoke
results are retained in `delivery.json`, `*-smoke.json` and the PR/FR; platform or
live-service combinations without a successful run are not claimed as tested.

Private destination: `ghcr.io/gbaudrit/agentstration-extension-llama-cpp:sha-<full-source-sha>`.
Before application publication, use a package-owner identity with verified
read/write package access to initialize a missing destination only with an empty
`FROM scratch` image and reserved `registry-init-*` tag. A 404 alone does not prove
absence. Verify **Private** before and after repository association, and grant
**Manage Actions access → gbaudrit/agentstration → Write** at
[package settings](https://github.com/users/gbaudrit/packages/container/agentstration-extension-llama-cpp/settings).
The bootstrap secret is confined to this initialization; normal
`oci-images.yml` dispatch uses `extension=llama-cpp`, `publish_preview=true` and
job-scoped `GITHUB_TOKEN`. The publisher verifies privacy before/after copying the
same tested archive with digests and provenance/SBOM attestations preserved.
If package administration is missing, complete code/tests/PR first and record that
remaining step rather than attempting an application push.

Deployment selector label: `preview:extension:llama-cpp`. Reuse
`preview-extension-labels.yml` and `apply-issue-labels.ps1 -PreviewExtensionsOnly`;
the main workflow derives the label after catalogue merge, while the same script
can provision it from this implemented branch beforehand. Preserve work-tracking
`extension:*` labels. Label existence does not publish an image or deploy anything;
deployment requires a privately published, resolvable digest. Do not automatically
assign the label to issues or trigger deployment during packaging qualification.

Docker Hub is deferred to [#1384](https://github.com/gbaudrit/agentstration/issues/1384),
including matching release-version/tag and main ancestry, same-archive promotion,
digest/provenance/SBOM receipts, anonymous pull and lifecycle qualification,
release-only credentials, retries and immutable-tag conflict behavior. Do not
create `extension/llama-cpp/v0.1.0`: that release path also triggers Docker Hub.
Keep #1292 open until effective private qualification is recorded; use no
premature auto-closing keyword.

## Utilities private image

Related to [#1296](https://github.com/gbaudrit/agentstration/issues/1296).
`deploy/images/extensions/utilities.json` is an implemented AEP catalogue entry with
independent version `0.1.0`. Its version file drives the project assembly, protected
AEP discovery identity and OCI metadata. AEP protocol and contribution versions
remain independent.

The image has no provider endpoint, credential, model or external-service
requirement. It exposes `hash.compute`, `json.compact`, `text.upper` and the existing
`json-schema.validation` contribution through MCP server `utilities` at `/mcp`.
The extension version is independent from schema contribution version `1.0`.
The dedicated `Agentstration.Extensions.Utilities.Tests` project runs native
MCP discovery/invocation, schema contracts and protected-discovery packaging tests
without the product composition. The OCI filter selects this project namespace.
`Agentstration.Api.Tests/McpToolCatalogTests.cs` remains product integration coverage
and retains transverse validation; it is not claimed as extension-owned input.

### Authentication, state and publication

Use the existing AEP SharedKeyFile settings: `Aep__EnrollmentMode=SharedKeyFile`,
`Aep__SharedKeyFile__Path`, `AuthorityUrl`, `PublicEndpoint`, and `StateFile`.
Persist the extension state file in its own writable directory, owned by the
process UID; mount the per-installation shared key read-only, mode 0600. The
authority has its own database/state mount and the matching per-extension trust.
Do not share server data volumes or put secrets in the image. HTTP requires an
explicit isolated Development allowance; use HTTPS for normal remote traffic.

The generic smokes check liveness, protected discovery, invalid bearer/proof
rejection, real SharedKeyFile enrollment, idempotent reannouncement, restart and
identity retention after recreation of both extension and SQLite authority.
The qualification baseline is Linux amd64, .NET 10, current source/server revision,
SQLite and deterministic fixtures. Actual source/digests, pinned bases and smoke
results are retained in `delivery.json`, `*-smoke.json` and the PR/FR; platform or
live-service combinations without a successful run are not claimed as tested.

Private destination: `ghcr.io/gbaudrit/agentstration-extension-utilities:sha-<full-source-sha>`.
Before application publication, use a package-owner identity with verified
read/write package access to initialize a missing destination only with an empty
`FROM scratch` image and reserved `registry-init-*` tag. A 404 alone does not prove
absence. Verify **Private** before and after repository association, and grant
**Manage Actions access → gbaudrit/agentstration → Write** at
[package settings](https://github.com/users/gbaudrit/packages/container/agentstration-extension-utilities/settings).
The bootstrap secret is confined to this initialization; normal
`oci-images.yml` dispatch uses `extension=utilities`, `publish_preview=true` and
job-scoped `GITHUB_TOKEN`. The publisher verifies privacy before/after copying the
same tested archive with digests and provenance/SBOM attestations preserved.
If package administration is missing, complete code/tests/PR first and record that
remaining step rather than attempting an application push.

Deployment selector label: `preview:extension:utilities`. Reuse
`preview-extension-labels.yml` and `apply-issue-labels.ps1 -PreviewExtensionsOnly`;
the main workflow derives the label after catalogue merge, while the same script
can provision it from this implemented branch beforehand. Preserve work-tracking
`extension:*` labels. Label existence does not publish an image or deploy anything;
deployment requires a privately published, resolvable digest. Do not automatically
assign the label to issues or trigger deployment during packaging qualification.

Docker Hub is deferred to [#1384](https://github.com/gbaudrit/agentstration/issues/1384),
including matching release-version/tag and main ancestry, same-archive promotion,
digest/provenance/SBOM receipts, anonymous pull and lifecycle qualification,
release-only credentials, retries and immutable-tag conflict behavior. Do not
create `extension/utilities/v0.1.0`: that release path also triggers Docker Hub.
Keep #1296 open until effective private qualification is recorded; use no
premature auto-closing keyword.
