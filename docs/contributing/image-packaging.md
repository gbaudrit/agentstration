# Extension image packaging

[Packaging milestone #16](https://github.com/gbaudrit/agentstration/milestone/16)
owns common OCI build/publication tooling and independently versioned extension
images. [Preview environments #15](https://github.com/gbaudrit/agentstration/milestone/15)
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

The PostgreSQL extension provides document storage and is distinct from the
optional server relational storage profile. The MAF Worker is an AWP executable,
not an AEP extension. Each packaging issue has a native dependency on #1289;
these are independent milestone tasks, not sub-issues of the shared tooling task.
The labels also live in `.github/labels.json` for maintenance through the existing
`scripts/github/apply-issue-labels.ps1` command.

## Foundry pilot

The implemented catalogue currently contains only `foundry`, under
`deploy/images/extensions/foundry.json`. Its recipe reuses `Dockerfile.extension`
for Linux amd64. Future entries are added when their actual build/smoke vertical
is delivered; tracking an issue does not imply an image is already published.

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

Product **CI** calls the reusable **Validate extension OCI image** workflow for
Foundry image inputs. The required `build-and-test` check includes its result;
Foundry-only changes use this targeted validation while shared or mixed inputs
retain the transverse checks described in [Testing](testing.md#ci-inventory-and-execution-safeguards).
The reusable workflow has read-only repository access and contains no publication
job. There is one PR image build, not a second independently triggered image run.

The **Extension OCI images** delivery workflow reuses that same validation for
extension release tags and manual dispatch. A manual dispatch takes an implemented
catalogue ID and defaults to validation only. Opting into `publish_preview`
publishes the tested archive to private GHCR after the build/smoke job succeeds.

The build pins SDK and ASP.NET base images by digest, requires a clean source
checkout, and emits an OCI archive, metadata and `delivery.json`. It includes OCI
source/version/revision labels, SBOM and provenance. The delivery records the
archive checksum and OCI digest. Build outputs are stored under ignored
`artifacts/`; credentials and generated state are excluded from build context.

Skopeo stages that exact archive in a disposable loopback-only registry. The
image smoke checks health, unauthenticated discovery rejection, authenticated AEP
identity/version/contribution, OCI labels and restart using a temporary
SharedKeyFile mounted read-only. It makes no Foundry/model request and contains
no Azure credential.

For Foundry, the same validation job also builds a disposable authoritative
server from the same source revision, using the generic Dockerfile and the same
pinned SDK/runtime bases. `scripts/packaging/foundry.compose.yml` runs only that
server and the exact staged Foundry digest. The server uses SQLite, deterministic
AI and the existing Development bootstrap with a newly generated disposable
administrator password. The test network is internal; no local inference server,
Worker, Azure resource or downstream credential is needed for enrollment.

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
  --authority-image agentstration-foundry-smoke-authority:local \
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
