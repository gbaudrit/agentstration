# Preview automation and cleanup

The **Preview reconciliation** workflow extends the existing manual/command
preview lifecycle. All tooling stays outside Runtime and runs from trusted `main`
with the existing protected `preview-deployment` environment.

## Automatic PR opt-in

1. A maintainer with repository write access adds **`preview:auto`** to an open,
   non-draft, same-repository PR. The existing preview-label synchronizer creates
   this label after merge. Bots and users without current write access cannot
   authorize this opt-in, even if they can apply labels.
2. Select optional AEP extensions using the existing **`preview:extension:<id>`**
   labels. SQLite and size profile 1 are the fresh-environment defaults. Existing
   active previews retain their recorded database, target, data and expiry anchor.
3. Publish the server image for the PR's exact head using **OCI images**,
   `extension=server`, `publish_preview=true`. Selected independent Worker/AEP
   images must also be privately published. Reconciliation does not build images
   or publish untrusted PR code with cloud credentials.
4. Reconciliation creates/updates stable `pr-N`, using frozen private image
   receipts and exact digests. Live opt-in approval/head/labels are checked again
   under the provider lease. A single automatic feedback card reports the result;
   command-specific cards remain historical records of their own commands.

Reconciliation runs at minutes 7, 22, 37 and 52 of each hour. A successful manual
**OCI images** workflow run from this repository also triggers it. GitHub's
schedule/queue and environment approval settings can delay execution. No PR event
workflow receives cloud credentials, and candidate workflow artifacts are ignored.

An unavailable image or full capacity produces a **waiting** card/result. No
fallback image, hidden rebuild or expiry renewal occurs. A new
head or changed extension labels are applied after the exact images are available.
Unchanged intent is not redeployed on every poll or independent image release.
Active automatic previews receive a live status/protection check. Failed health,
missing access or a publication that failed after guest startup is retried through
ordinary data-preserving deploy. No recovery path silently resets application data.
The opt-in declares running desired state; preserve-only maintenance belongs to
manual previews without automatic opt-in. After opt-out cleanup, an explicit new
manual deployment clears the old automatic association.

## Deployment labels

| Label | Behavior |
| --- | --- |
| `preview:auto` | Create one PR preview, then update published new heads or changed extension selection while preserving data |
| `preview:auto:redeploy` | In addition to `preview:auto`, fully reset and recreate the environment for each newly requested update |
| `preview:keep` | Suspend automatic expiry, PR-close and opt-out cleanup until explicit destroy |
| `preview:status:deployed` | A running preview has verified protected access |
| `preview:status:pending` | An admitted operation is queued or waits for images/capacity |
| `preview:status:error` | An operation failed, a probe failed or preview health/protection is bad |

`preview:auto:redeploy` is a persistent modifier, not a standalone trigger. Adding
it to an existing automatic preview requests one reset. Keeping it does not reset
the preview on every poll; a new head, extension selection or opt-in/reset-label
grant requests another update. Removing the modifier returns to data-preserving
updates. Same-intent health/startup/access retries always preserve data, even in
reset mode. Image selection and capacity checks finish before any reset starts.
Both automatic and reset-mode grants require a human with current write access.

`preview:keep` also requires a verified human write-access grant, and works on the
owning PR or issue for a manually deployed preview. It does not create an
environment, restart stopped compute, renew expiry, exempt quotas or stop opted-in
updates/resets. It preserves the environment even after the PR is closed or
`preview:auto` is removed. `/preview destroy` remains available. Removing keep
immediately restores the recorded cleanup rules on the next reconciliation, so
an already-expired preview is removed. A deletion that has already started cannot
be rolled back by adding keep; reconciliation finishes its persisted intent.

`preview:status:*` labels are output, never deployment authorization. The three
signals are independent: an old version can remain deployed while its requested
update is pending or failed. A waiting attempt does not introduce an error.
Pending/error updates without a new observation preserve last verified availability.

`preview:status:deployed` reflects verified availability. Successful command
results add/remove it immediately; reconciliation repairs it from live status,
including manual previews and closed retained PRs. It is removed for stopped,
unhealthy, unprotected, absent or removed previews and incomplete cleanup. A failed
probe without a verified result leaves the label unchanged until a later check;
the command card and workflow provide error details. Direct Actions deployments
without command tracking are reflected on the next reconciliation. Other labels
are preserved. Do not maintain status labels manually.

A successful deployment clears pending and error; confirmed full cleanup clears
all status labels. A healthy `/preview status` does not erase an unresolved
operation error or a waiting update. Automatic healthy polls also retain the
existing waiting/error card and its remediation link until a verified operation
resolves it. Health-only errors are repaired by a verified
redeployment (automatic when opted in). A new waiting request can coexist with a
previous unresolved error until deployment or cleanup confirms recovery. A failed
probe adds error while retaining last verified availability until a fresh result
is obtained. Command cards retain operation, requested revision, phase/reason and
next action; use `/preview status` for the currently observed version.

The label synchronizer renames the old `preview:deployed` repository label when
its replacement does not exist, preserving assignments. If both names already
exist, per-environment reconciliation removes the legacy assignment after a
verified transition. Neither status label triggers deployment or extension selection.

## Cleanup and deletion status

Recorded expiry now applies to **all managed Azure previews**, including manually
created environments and those created before automation merged, unless retained
by an authorized `preview:keep` grant. Expired previews
are fully reset, including application data, VM, OS/data disks, IP and owned
Cloudflare tunnel/DNS. Shared storage, the resource group, Access application and
role assignments remain.

Without `preview:keep`, PR close/merge removes its preview. Removing `preview:auto` removes an
automatically managed preview. Opt-out is not inferred for an ordinary manual
preview that never opted into automatic updates. `/preview destroy` still performs
an immediate explicit reset; `/preview` updates while preserving data, and
`/preview redeploy` explicitly resets and recreates.

`/preview status` distinguishes:

| State | Meaning |
| --- | --- |
| No recorded deployment | No ownership record or namespace resources were found; no synthetic image selection is made |
| Stopped | Preserved data/resources remain, and may still incur charges |
| Cleanup incomplete | A cleanup intent, owned resources or route remains; retry full reset |
| Removed | Both Azure and Cloudflare cleanup are confirmed, with known completion time and reason |

Deletion time is the **actual recorded completion**, not expiry. Repeated reset
retains it. Legacy deleted records without that field display `not available`.
New deployment creates a new owner/expiry and clears prior deletion state.
The status path does not resolve tags or require a surviving PR head.

An expired or manually removed automatic preview stays removed for unchanged
head/opt-in intent. Push a new head or explicitly remove/re-add `preview:auto`
to request a fresh lifecycle. An old manually deleted record needs an opt-in
newer than its known deletion time; if that historical time is unknown, create
explicitly with `/preview` before enabling automatic updates.

## Capacity reservations

Optional variables belong in **Settings → Environments → preview-deployment →
Environment variables**, not repository variables:

| Variable | Default | Bound |
| --- | --- | --- |
| `AZURE_PREVIEW_MAX_ENVIRONMENTS` | `2` | `1`–`100` reserved environments |
| `AZURE_PREVIEW_MAX_VCPUS` | `4` | `1`–`400` reserved vCPU |

Both manual and automatic deployments acquire the same independent storage-blob
capacity lease before provisioning. The existing environment lease remains in
place. A same-environment update replaces its reservation, rather than counting
twice. Supported VM sizes remain `Standard_B2ms` (2 vCPU) and `Standard_B4ms`
(4 vCPU); a new larger size needs explicit qualification elsewhere.

Reservations include stopped/preserved environments, incomplete deployment or
cleanup, and residual owned resources. This conservatively bounds disks/IP as
well as VM allocations. Successful full reset releases the reservation. Cleanup
does not require capacity admission, so a full quota cannot prevent cleanup.
These are automation limits, not an Azure spending cap or currency estimate.

## Reconciliation and recovery

The scanner reads at most 1,000 ownership-namespace blob entries and bounded GitHub
pages. Inventory overflow fails closed. It checks saved ownership and the live
removal condition again under the lease. A delayed TTL/close scan cannot remove a
new lifecycle or a newly extended expiry. Failed cleanup persists intent and is
retried on a later pass. Failure of GitHub feedback does not undo confirmed cleanup.

Known Azure and Cloudflare leftovers are retried with their saved ownership.
Newly provisioned OS disks receive ownership tags only after verifying the owned
VM and actual disk attachment. Tagged detached disks can then be safely recovered.
An untagged detached legacy OS disk requires operator inspection. Unrecorded Azure
namespaces block new admission; unrecorded route candidates are reported, never
deleted blindly. Restore the original private ownership record to recover proof.

The existing infinite environment lease and the new `_preview-capacity.json`
lease deliberately survive runner termination. Before breaking either lease,
verify that no owning Actions/ARM operation remains active, inspect the saved
ownership/inventory, then follow the existing [Azure recovery procedure](preview-azure.md).
Never automatically break a lease or prune resources by prefix alone. The capacity
blob is shared; the environment blob is `pv-<scope-hash>/lifecycle.json`.

## Qualification

After merge, run **Actions → Preview reconciliation → Run workflow** on `main`.
**Dry run defaults to true**: it resolves metadata/images and lists intended
actions without changing previews or GitHub comments. The small public artifact
contains only environment IDs, operations, reasons, source identities and bounded
pending/failure codes. It contains no startup settings, credentials or raw logs.

Scheduled runs apply changes. To apply an explicit manual run, clear **dry_run**.
The first scheduled pass can remove already-expired previews; inspect their saved
expiry and add `preview:keep` with a human maintainer account before merging/enabling
this workflow if data needs to be retained. The old proposed `preview:enabled`
name is replaced by `preview:auto`; it does not authorize automation.

Qualify opted-in creation, a published new-head update with preserved data,
close/opt-out cleanup, expiry, capacity contention, reset-mode updates, keep across
closure/expiry, derived deployed labels and `/preview status` after
deletion. Test SQLite and PostgreSQL explicitly as appropriate. Finish by confirming
owned compute/routes are absent. #1286 remains open until that live evidence and
its native child issue completion/waivers are recorded. Deployed Playwright remains
#1287; SSH/Docker remains #1288. See [ADR-0269](../decisions/0269-preview-reconciliation-enforces-owned-expiry-and-capacity.md).
