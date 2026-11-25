# Manual previews from GitHub Actions

The **Preview on demand** workflow is the maintainer form for
[#1284](https://github.com/gbaudrit/agentstration/issues/1284). It uses the existing
[portable lifecycle](preview-lifecycle.md), [Azure adapter](preview-azure.md) and
private GHCR packages. SQLite is the default; PostgreSQL is an optional overlay.
Deployment never builds or republishes an application image.

This increment deploys healthy isolated containers and supports explicit teardown.
Its [protected route](preview-access.md) returns an HTTPS URL only after fresh
protection and readiness checks. [#1285](https://github.com/gbaudrit/agentstration/issues/1285)
verifies Cloudflare Tunnel and Access protection. #1284 remains open for that
acceptance criterion. Automatic PR deployment/TTL cleanup is #1286; deployed
smoke/Playwright journeys are #1287. No Runtime deployment service is introduced.

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
at that exact source SHA, and the platform must be Linux amd64. Only the current
supported catalogue identities and AWP protocol are accepted. Source files are
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
| `ttl_hours` | Whole number 1–168, default 2; recorded expiry, not yet automatic cleanup |
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
resource IDs. There is no public application URL in this increment. Provider
errors retain safe error codes rather than exporting logs or credentials.

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
preserve/redeploy retains identities and the original expiry anchor.

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
