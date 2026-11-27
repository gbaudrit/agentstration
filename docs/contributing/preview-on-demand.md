# Manual previews from GitHub Actions

The **Preview on demand** workflow is the maintainer form for
[#1284](https://github.com/gbaudrit/agentstration/issues/1284). It uses the existing
[portable lifecycle](preview-lifecycle.md), [Azure adapter](preview-azure.md) and
private GHCR packages. SQLite is the default; PostgreSQL is an optional overlay.
Deployment never builds or republishes an application image.

The manual lifecycle has been qualified with SQLite and PostgreSQL, including
protected HTTPS, preserve/restart and reset. Its [protected route](preview-access.md)
returns an HTTPS URL only after fresh protection and readiness checks. The
remaining same-tenant non-member check is tracked in
[#1285](https://github.com/gbaudrit/agentstration/issues/1285).
Issue/PR commands and [scheduled reconciliation](preview-automation.md) implement
the external automation in #1286. Deployed smoke/Playwright journeys remain #1287.
No Runtime deployment service is introduced.

Prepare the [shared Access configuration](preview-access.md) and run its opt-in,
read-only audit before the first route deployment. It uses the same protected
environment without creating cloud resources or publishing a URL.

## Prepare a published selection

Use the **OCI images** packaging workflow to publish the desired server source
revision to private GHCR before deploying it. The server and MAF Worker are
independently versioned components: publishing the server does not require
rebuilding the Worker. Both packages must grant this repository **Actions access:
Write** for publication; deployment uses only `packages: read` and the ephemeral
`GITHUB_TOKEN`. No bootstrap PAT is needed.

The form accepts a server branch, tag, full commit SHA, or `pr:NUMBER` for a PR in
this repository. A branch/PR is resolved once to its exact current SHA. An image
must have been published **for that SHA**. A newer unbuilt commit never falls back
to an older image. Fork PRs are refused in this first manual entrypoint.

The Worker defaults to `published`: the newest GitHub-listed tagged Worker image,
excluding untagged attestation manifests. Alternatively select its own branch,
tag, SHA or same-repository PR. This choice is independent of the server revision.
The selected index digest is frozen before platform-specific config inspection.
OCI source/version labels must match the component catalogue and version file
at that exact source SHA, and the platform must be Linux amd64. Core catalogue identities and AWP protocol stay fixed. A new AEP catalogue may
come from its privately published image’s source before its packaging PR merges;
its namespace, identity, AEP protocol and contribution remain strictly validated. Source files are
bounded metadata; their scripts, project files and MSBuild logic are never run.

An absent image produces a publication instruction. A moved SemVer tag with the
wrong SHA, a public package, an incompatible Worker or an inconsistent digest is
an error, never a reason to rebuild or silently select another component.

## Run the form

In **Actions → Preview on demand → Run workflow**, select **main** as the workflow
branch. The application revision is a separate field; this permits deploying
published PR code while executing only trusted deployment scripts from `main`.
The existing `preview-deployment` environment stays restricted to `main`.

| Input | Meaning |
| --- | --- |
| `operation` | `validate` (default), `deploy`, `status`, or `destroy` |
| `environment_id` | Stable lowercase identifier, at most 40 characters; use a different ID for another preview, e.g. `pr-123` |
| `revision` | Already-published server revision, default `main` |
| `provider` | `azure-vm`; future providers reuse the portable contract |
| `database` | `sqlite` (default) or `postgresql`; changing a live database requires reset first |
| `vm_size_profile` | Environment variable slot 1–5, default 1 |
| `ttl_hours` | Whole number 1–168, default 2; expiry enforced by scheduled reconciliation |
| `data_mode` | `preserve` (default), or explicit `reset` on `destroy` only |
| `test_profile` | `readiness` (default) or `none`; exact-image/healthy-container checks remain mandatory |
| `worker_revision` | `published` (default) or an independent already-published Worker revision |

All Azure identity, storage and platform variables remain in **the GitHub
environment**, including `AZURE_PREVIEW_OS_PROFILE`, `AZURE_PREVIEW_IMAGE_VERSION`
and `AZURE_PREVIEW_VM_SIZE1` through `SIZE5`. Empty/unsupported slots are refused.
Only slots the maintainer configures are usable. The optional environment variable
`AZURE_PREVIEW_REGION` defaults to `westeurope`. OS/version/size resolve once into
the public canonical request; values are never resolved again during execution.

`validate` resolves and checks real private image metadata but does not authenticate
to Azure or create infrastructure. The older **Preview Azure environment** JSON
entrypoint remains available for advanced callers.

For example, after the workflow is merged to `main`:

```bash
gh workflow run preview-on-demand.yml --ref main \
  -f operation=validate -f environment_id=manual-example \
  -f revision=EXACT_PUBLISHED_SERVER_SHA

gh workflow run preview-on-demand.yml --ref main \
  -f operation=deploy -f environment_id=manual-example \
  -f revision=EXACT_PUBLISHED_SERVER_SHA -f database=sqlite
```

The run summary and small public request/result artifacts identify the selected
source SHA, original GHCR digests, database, lifecycle state, expiry and owned
resource IDs. A protected HTTPS URL is included after live protection checks. Provider
errors retain safe error codes rather than exporting logs or credentials.

## Request a preview from an issue or PR

A repository maintainer can post one plain-text command in an ordinary issue
or a same-repository PR after the command workflows are merged to `main`:

```text
/preview
/preview deploy
/preview deploy database=postgresql
/preview deploy ref=pr:123
/preview deploy ref=main database=postgresql
/preview redeploy
/preview redeploy database=postgresql
/preview status
/preview destroy
```

`/preview` alone is an exact shorthand for `/preview deploy`, including its
data-preserving defaults. Arguments require an explicit `deploy` or `redeploy`.

On a PR, `/preview deploy` selects **that PR's own head commit** and its
`preview:extension:*` labels. No `ref` is needed. Use `database=postgresql` only
when that database profile is wanted; SQLite remains the default. Another PR,
branch or SHA cannot be selected from a PR comment: use an ordinary issue or the
manual form for that purpose. Fork PR deployment is refused.

On an ordinary issue, without `ref`, discovery uses GitHub's native linked **open** PRs that close the
issue (the Development relationship), through `closedByPullRequestsReferences`.
A plain mention in an issue body/comment does not select code. Exactly one linked
same-repository PR selects its current head SHA. Multiple linked PRs produce a
numbered choice and no deployment; no linked open PR produces a request for an
explicit reference. Closed PRs are ignored during automatic discovery. An
explicit `ref` accepts the same branch/tag/full SHA/`pr:NUMBER` forms as the manual
workflow. Fork PRs are refused. An image must already exist for the exact selected
SHA; this command does not build or publish one and never falls back to another
revision. A moving branch/PR is frozen once at admission.

Only `ref` and `database` are supported on `deploy` and `redeploy`. Other arguments, duplicate
keys, multiple commands and shell/Markdown syntax are refused. SQLite, size
profile 1, two recorded TTL hours, mandatory readiness and the independently
published Worker are the defaults. Other combinations remain available in the
manual form. `status` and `destroy` accept no arguments and reuse the recorded
selection, even when refs or environment defaults have changed.

The stable environment is `pr-N` for PR comments, with protected hostname
`dev-pr-N.agentstration.cloud`; ordinary issues retain `issue-N` and
`dev-issue-N.agentstration.cloud`. These are distinct environments even when an
issue links to that PR. Repeated deploy commands update that environment
with data preservation. After pushing to a PR, publish its exact server image
and post a **new** `/preview deploy` comment to update the same environment; pushes
and label changes alone do not deploy. A command already admitted keeps its original
SHA even if the branch moves. Changing database requires a full reset first.
**`/preview destroy` fully resets owned infrastructure and application data**,
including disks and public IP. The manual form still offers explicit preserve
mode for recovery/maintenance.

**`/preview redeploy` deletes the previous preview and all its application data,
then deploys a fresh environment.** It resolves the current PR head and labels
using the same private image checks as deploy, before any deletion. It can select
a new database after the reset; it never converts the previous data. Feedback
explicitly describes the data deletion before execution. The reset and fresh
deployment share one provider lease, ownership checks and environment queue.
The old route is removed, owned VM/disks/IP are deleted, and a new owner, expiry
and protected route are created. Shared resources remain. An absent preview is
created normally. A failed or incomplete reset prevents deployment. If recreation
fails after reset, the previous data is already gone: check `/preview status`,
then use `/preview` to resume without another reset. This command requires an
open issue/PR and is reauthorized just like deploy. See
[ADR-0268](../decisions/0268-explicit-preview-redeployment-resets-owned-state.md).

The admission workflow runs only default-branch tooling, checks the author's
current repository write/maintain/admin permission and ignores untrusted users
and bot comments. GitHub association labels alone do not grant permission.
Commands edited after creation are refused; post a new comment. Deployment
requires an open issue or PR; status/destroy also work after closure or merge,
including after deletion of the PR head branch/repository. Permissions
and the original command are checked again in the receiving manual workflow and
immediately before its selection announcement. Deleted/edited commands and revoked
permissions stop execution; maintainers can still use the manual form for cleanup.

Admission dispatches **Preview command deployment** on `main`, which reuses
**Preview on demand**. Its single JSON input avoids changing the existing ten-input
manual form. Admission has GitHub dispatch/comment permissions, no protected
environment, Azure OIDC or cloud credentials. The receiving workflow resolves
private published image digests and protected defaults using the existing selector,
then updates **one bot-owned comment per command**, created before dispatch.
Infrastructure execution proceeds only after that verified-selection update
succeeds. Later updates replace this card's state rather than posting metadata
in several comments. Ownership and a selection marker prevent overwriting human
comments, another bot's comment or another command's card.

The card shows the requested/checked/ready or failed state, linked server commit,
database, selected extensions, known VM/region and workflow link. Full image
versions/digests are collapsed. A successful readiness check adds the protected
application URL and recorded expiry; the card describes scheduled full cleanup
and offers `/preview destroy` for earlier removal. An active VM without
confirmed protected access does not get an access link.

Completed checks show a table for server, Worker, selected extensions and the
optional PostgreSQL service: container state, Docker health and restart count.
The values are a live snapshot, not an availability history. A missing container
is identified; duplicate/unknown states cannot claim health. When the VM is
stopped and no container observations are available, the table says `not observed`.
Status includes the current trusted collector in Run Command, so older running
previews do not require a redeployment just to obtain this table. Only bounded
enum/counter fields are collected; inspect configuration, health logs, error
messages and private settings are never exported. Access remains verified before
publishing a URL; the redundant provider-identification sentence is omitted.

Result reporting downloads the existing public lifecycle-result artifact from
the same workflow run and attempt, then checks its environment, frozen images,
revision and protected URL before updating the card. It does not depend on the
full JSON job output: Azure login masks subscription IDs, and GitHub can suppress
an output containing those IDs even after a successful deployment. Private
extension settings and credentials never enter this artifact or reporting job.

Failures explain the stage and next action. For example, a missing server image
asks for **OCI images**, `extension=server`, `publish_preview=true` on the requested
revision; publishing only an extension is insufficient. Inaccessible private
packages point to package visibility and Actions access. Failures before execution
state that Azure deployment was not started; execution failures recommend status
and cleanup because partial resources may remain. Provider responses, private
extension configuration, credentials and unverified URLs are never copied into
comments. A new AEP extension does not need its catalogue merged into `main`:
the selector reads bounded catalogue/version data at the published image's exact
source SHA. Deployment scripts and Compose still come from trusted main.

These workflows use the ephemeral `GITHUB_TOKEN`; no new PAT or Azure/Cloudflare
secret is required. Repository policy must permit the requested Actions dispatch
and comment write permissions: `issues: write` for ordinary issues and
`pull-requests: write` for PR conversations. Admission, announcement and result
reporting need the corresponding write permission; the reusable deployment caller
must also grant it because called workflows cannot elevate the caller token.
Ownership-only checks retain read access. A failed GitHub call identifies its
fixed stage (comment lookup/publication, permission check, dispatch or metadata
lookup), while withholding remote responses and comment content.
Caller secret inheritance remains explicit
through both reusable workflows; cloud credentials stay in the existing protected
executor. The main-only provider event boundary remains unchanged. See
[ADR-0263](../decisions/0263-issue-preview-commands-dispatch-trusted-manual-workflows.md)
and [ADR-0266](../decisions/0266-pr-preview-commands-use-frozen-heads-and-owned-feedback.md).

The [reconciliation workflow](preview-automation.md) enforces expiry, PR opt-in
and close/opt-out cleanup, capacity reservations and owned orphan recovery.
Manual and issue operations share the same environment lease and capacity
admission. `/preview status` reports confirmed deletion time/reason, retained
resources or incomplete cleanup instead of treating every missing VM as absence.

### Choose optional AEP extensions with issue labels

Add `preview:extension:foundry` to the issue or PR, then post `/preview deploy`. The
live label list is frozen with the command and rechecked before the image digests
are announced. A label change while the run is queued requires a new command.
Removing a label and posting `/preview deploy` stops that extension. Labels alone
do not trigger deployment. No deployment label means server + MAF worker only.
The `extension:*` backlog labels do not affect the deployed selection.

Each selected AEP image must be privately published with an implemented
`deploy/images/extensions/<id>.json` catalogue at its exact published source SHA.
The catalogue may belong to an open packaging PR and need not exist in `main` yet.
For example, OpenAPI can be previewed on its packaging PR before merge. A missing
source catalogue, public/unreadable package or absent tagged image fails before
provisioning. Admission freezes well-formed IDs; selection verifies publication. Each
extension defaults to its own newest tagged published delivery, independently of
the server PR, and is frozen by digest. There is no implicit local inference.
The digest announcement is the reviewable selection for that run.

The **Preview extension labels** workflow synchronizes selectable labels when
catalogue changes merge to `main`, using only repository issue permissions. It
does not publish images or deploy resources. For a manual inspection/retry:

```powershell
./scripts/github/apply-issue-labels.ps1 -Repository gbaudrit/agentstration -PreviewExtensionsOnly -DryRun
./scripts/github/apply-issue-labels.ps1 -Repository gbaudrit/agentstration -PreviewExtensionsOnly
```

The script derives deployment labels from the catalogue. A new compatible AEP
package needs no extra deployment workflow, arbitrary Compose file, or provider
code. Its catalogue supplies `extensionId`, AEP version and expected contribution;
its image implements the SDK SharedKeyFile settings and `/var/lib/aep/instance`
persistence used by the existing packaging enrollment smoke. The candidate's
version path must stay under `src/extensions/<project>/version.txt`; its image
name is fixed to `extension-<id>`. Source build/project files are never executed
or copied into the guest transfer. Only canonical public receipt fields become
frozen catalogue snapshots. Existing main catalogue identities and all persisted
AEP identities remain protected against substitution or aliasing. See
[ADR-0267](../decisions/0267-pre-merge-aep-previews-consume-published-catalogue-data.md).
The common recipe
provides internal authority/discovery URLs, a unique read-only key and a private
state volume. It supports Linux amd64 images from `Dockerfile.extension`.

The deployment verifies actual authenticated enrollment before reporting a URL,
including before manual Agentstration initialization. Selecting Foundry makes its
provider contribution available; configure connections/models through the normal
product paths afterwards. Selecting the PostgreSQL AEP extension does not change
`database=sqlite` into PostgreSQL. Selecting Ollama does not provision an inference
backend or download a model. Its current image reads `Ollama:Endpoint` at startup;
supply that setting through the private configuration below. Other extension
settings follow their image documentation.

#### Private startup configuration

Optional settings belong in the **`preview-deployment` environment secret**
`PREVIEW_EXTENSION_CONFIGURATION`, as a JSON object keyed by catalogue ID. For
example, an already-running Ollama endpoint can be selected without provisioning
local inference:

```json
{
  "ollama": {
    "Ollama": { "Endpoint": "https://ollama.example.com/" }
  }
}
```

The map is limited to 16 KiB. Only selected entries are sent to the guest. Each
extension receives its own mode-0600, read-only `/app/appsettings.Preview.json`;
the authority and Worker do not receive those settings. Use the option names
supported by the image. Managed `Aep` enrollment settings cannot be overridden.
The input is removed from the process environment before Docker is invoked.
Azure carries it through ARM protected parameters, outside OCI bundles, public
requests, issue feedback and artifacts. Deployment credentials are never copied
into extension configuration automatically. Foundry needs no new startup secret
for health/enrollment; downstream connections/models remain product configuration.

Change the secret and post a new deploy command to apply new settings. A private
configuration reference changes to recreate the affected container, retaining its
key and persisted instance. Status/destroy use the recorded host configuration
without reading the current secret. Deselection removes the private settings file
and retains the enrollment key/data; reactivation receives the selected settings
again. The existing Docker CLI also accepts a private JSON file through
`lifecycle.py deploy --extension-configuration /path/to/settings.json`.

Redeploy/recreation preserves the enrollment key and instance. Deselection retains
that state for reactivation; existing registrations and user definitions remain
in Agentstration and may be disconnected. Full `/preview destroy` removes owned
volumes, disks and IP. Status/destroy use the recorded selection even if labels,
tags or catalogue entries have subsequently changed. Legacy core-only version 1
requests remain supported; selected extensions use version 2 public receipts.
The original manual form remains unchanged and core-only. Advanced canonical
requests can include an `extensions` map. See
[ADR-0265](../decisions/0265-preview-labels-select-independently-delivered-aep-extensions.md).

### Qualify issue commands after merge

The offline tests fake GitHub, registry and cloud APIs. After merge, use a
disposable issue and already-published same-repository PR to verify:

1. Missing/multiple links give selection guidance without a privileged deployment.
2. A unique native link freezes its head SHA; an explicit reference bypasses
   ambiguous discovery. The SHA and original digests appear before execution.
3. A real non-writer command has no dispatch/feedback side effect; changed commands
   and revoked author permissions fail closed.
4. A successful deploy returns the protected URL; a missing image or failed
   announcement/execution yields a safe failure rather than a URL.
5. Status uses the recorded state; destroy removes only that issue's infrastructure,
   disks/IP/data and route. Run the original manual form as a compatibility check.

Record actual workflow/run evidence in #1382. New workflows cannot be qualified
through the protected main boundary before merge. No live cloud deployment is
claimed by PR preparation.

## Inspect and remove a preview

For `status` and `destroy`, only the stable environment ID and destroy data mode
matter. Current revision, database, size, TTL and Worker fields do not override
the recorded selection. These jobs read the private ownership blob using OIDC,
without contacting GHCR; expired tags or changed GitHub defaults cannot prevent
teardown. A missing or incompatible ownership record is refused with diagnostics;
resources are never reconstructed or adopted from names alone. The provider
rechecks the saved selection and physical resource ownership under its lease.

```bash
gh workflow run preview-on-demand.yml --ref main \
  -f operation=status -f environment_id=manual-example

# Stops containers and deallocates the VM; retains billable disks/IP and identities.
gh workflow run preview-on-demand.yml --ref main \
  -f operation=destroy -f environment_id=manual-example -f data_mode=preserve

# Deletes only this preview's owned infrastructure and data.
gh workflow run preview-on-demand.yml --ref main \
  -f operation=destroy -f environment_id=manual-example -f data_mode=reset
```

Reset waits up to two minutes for Azure inventory convergence after deletion.
If inventory remains stale, check status and retry reset. Shared storage, its
ownership record, the resource group and bootstrap role assignments remain.
Repeated reset is safe. A later deploy after reset creates a new data lifecycle;
ordinary data-preserving deploy retains identities and the original expiry anchor.
The explicit `/preview redeploy` command performs a reset and starts a new anchor.

Actions queues operations by environment ID without cancelling an operation in
progress. The provider's independent concurrency group and exclusive state-blob
lease also protect against overlapping advanced entrypoints. A competing change
to the recorded images/target causes a safe refusal; run status again. For a
runner terminated while holding its infinite lease, follow the
[Azure recovery procedure](preview-azure.md) after inspecting the original run.

## Validation

The default Python suite uses fake API, registry and cloud boundaries and requires
no network, cloud credentials or model. The **Preview deployment contracts** workflow
also offers an optional `published_revision` dispatch input to check the real
private GHCR selector. That opt-in job uses only repository/package read access,
uploads public selection metadata and has neither OIDC permission nor an Azure
environment. Its target is a contract fixture, not a deployable operator request.


### Qualify PR commands and feedback after merge

1. Publish a private server image for an open same-repository PR's exact head SHA.
   If extensions are wanted, publish their private images with valid source
   catalogue/version metadata (packaging may remain unmerged); apply the desired `preview:extension:*` labels to the PR.
2. As a human maintainer, post `/preview deploy` on that PR. Confirm admission is
   not skipped, dispatch runs on `main`, one feedback card advances through checks
   to readiness, and the protected `dev-pr-N.agentstration.cloud` URL works.
3. Check the card's commit, database, selected extensions, expiry warning and
   collapsed image details against the public request/result artifacts.
4. Push a new commit, publish its server image, then post a new deploy command.
   Confirm the same `pr-N` environment updates without resetting data. The older
   command's card must retain its own revision.
5. Post `/preview status`, including after PR closure. Confirm it reads saved
   state. Post `/preview destroy` and confirm removal of owned VM, disks, IP and
   application data; the shared services remain. Do not leave billable resources.
6. Qualify a missing exact server image: no Azure execution should start, and the
   card must explain which image to publish. Check a refused fork or revoked
   requester without granting extra permissions. Never publish fake human commands
   using an automation identity to simulate this event qualification.

These actual GitHub/Azure checks are pending until trusted-main merge; offline
unit tests do not establish event delivery or cloud readiness.

## GUID configuration selected by a PR

Use the visible ordered **Preview configuration** subsection documented in [Additional settings and GUID deployment profiles](../reference/additional-settings.md). Selection is frozen with source/image identities and saved for status/destroy. Metadata-only changes preserve data; explicit redeploy retains its reset contract.
