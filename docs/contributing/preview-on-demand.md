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
Issue comment commands are the first automation slice of #1286 (#1382).
Automatic PR-label deployment/TTL cleanup remains #1286; deployed smoke/Playwright
journeys are #1287. No Runtime deployment service is introduced.

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
resource IDs. A protected HTTPS URL is included after live protection checks. Provider
errors retain safe error codes rather than exporting logs or credentials.

## Request a preview from an issue

After the issue-command workflows are merged to `main`, a repository maintainer
can post one plain-text command in an ordinary issue:

```text
/preview deploy
/preview deploy database=postgresql
/preview deploy ref=pr:123
/preview deploy ref=main database=postgresql
/preview status
/preview destroy
```

Without `ref`, discovery uses GitHub's native linked **open** PRs that close the
issue (the Development relationship), through `closedByPullRequestsReferences`.
A plain mention in an issue body/comment does not select code. Exactly one linked
same-repository PR selects its current head SHA. Multiple linked PRs produce a
numbered choice and no deployment; no linked open PR produces a request for an
explicit reference. Closed PRs are ignored during automatic discovery. An
explicit `ref` accepts the same branch/tag/full SHA/`pr:NUMBER` forms as the manual
workflow. Fork PRs are refused. An image must already exist for the exact selected
SHA; this command does not build or publish one and never falls back to another
revision. A moving branch/PR is frozen once at admission.

Only `ref` and `database` are supported on `deploy`. Other arguments, duplicate
keys, multiple commands and shell/Markdown syntax are refused. SQLite, size
profile 1, two recorded TTL hours, mandatory readiness and the independently
published Worker are the defaults. Other combinations remain available in the
manual form. `status` and `destroy` accept no arguments and reuse the recorded
selection, even when refs or environment defaults have changed.

The stable environment is `issue-N`, with protected hostname
`dev-issue-N.agentstration.cloud`. Repeated deploy commands update that environment
with data preservation. Changing database requires a full destroy first.
**`/preview destroy` fully resets owned infrastructure and application data**,
including disks and public IP. The manual form still offers explicit preserve
mode for recovery/maintenance.

The admission workflow runs only default-branch tooling, checks the author's
current repository write/maintain/admin permission and ignores untrusted users
and bot comments. GitHub association labels alone do not grant permission.
Commands edited after creation are refused; post a new comment. Deployment
requires an open issue; status/destroy also work on a closed issue. Permissions
and the original command are checked again in the receiving manual workflow and
immediately before its selection announcement. Deleted/edited commands and revoked
permissions stop execution; maintainers can still use the manual form for cleanup.

Admission dispatches **Preview issue deployment** on `main`, which reuses
**Preview on demand**. Its single JSON input avoids changing the existing ten-input
manual form. Admission has GitHub dispatch/comment permissions, no protected
environment, Azure OIDC or cloud credentials. The receiving workflow resolves
private published image digests and protected defaults using the existing selector,
then posts the source SHA, digests and run link in the issue. Infrastructure
execution proceeds automatically only after that announcement succeeds. A final
comment contains the verified protected URL/state or a bounded failure and run
link. Failure output never republishes provider responses or an unverified URL.

These workflows use the ephemeral `GITHUB_TOKEN`; no new PAT or Azure/Cloudflare
secret is required. Repository policy must permit the requested Actions dispatch
and issue-comment write permissions. Caller secret inheritance remains explicit
through both reusable workflows; cloud credentials stay in the existing protected
executor. The main-only provider event boundary remains unchanged. See
[ADR-0263](../decisions/0263-issue-preview-commands-dispatch-trusted-manual-workflows.md).

TTL is still **recorded only**. This slice does not implement automatic expiry,
PR close/label events, global quotas or orphan reconciliation; remove test previews
with `/preview destroy` after use. Manual and issue operations share the same
environment serialization, provider lease and ownership checks.

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
