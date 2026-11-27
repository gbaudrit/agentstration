# ADR-0269: Preview reconciliation enforces owned expiry and capacity

Status: Proposed — 2026-11-26

## Context

#1286 completes the external preview automation above #1382, #1388 and #1405.
Manual deployment, explicit reset and protected URLs already work. Expiry was
metadata only; there was no global admission bound or recovery of missed PR
events. Status discarded the persisted deletion flag when no VM remained.

## Decision

Run bounded reconciliation from trusted main on a 15-minute schedule, manual
invocation and successful same-repository manual OCI-image workflow completion.
Read current PR metadata and private ownership records; never consume candidate
workflow artifacts, source scripts or a candidate checkout with cloud credentials.
There is no `pull_request_target` deployment path or Runtime deployment service.

Automatic PR creation/update requires `preview:auto`, an open non-draft
same-repository PR and the latest label grant by a human with current repository
write access. Freeze the head, selected extension IDs and existing private image
receipts with the current selector. Missing publications and full capacity wait
without building images, allocating resources or extending expiry. Recheck live
approval/head/labels under the provider lease before mutation. Preserve data and
the current target/database on ordinary updates. The persistent
`preview:auto:redeploy` modifier requires its own verified human write grant and
requests full reset for a new head, extension selection or opt-in/reset-mode grant.
Persist that grant with the applied intent so polls and same-intent health retries
do not repeatedly erase data. Removing the modifier restores preserving updates.

Apply recorded expiry to all managed previews, including manually requested ones,
unless their owning PR/issue has an authorized human `preview:keep` write grant.
Keep suspends expiry, close and opt-out cleanup, including for closed PRs, until
explicit destroy or removal of keep. It does not start compute, exempt capacity or
prevent opted-in updates/reset mode. Finish already-started deletion intents;
retention cannot roll back a partial deletion.
Remove PR previews on close/merge and automatically managed previews on opt-out.
Use the existing owned full reset for Azure and Cloudflare. Persist minimal
deletion intent/completion metadata in the existing private lifecycle record.
Recheck owner and removal reason under the lease: a stale scan cannot delete a
recreated or extended lifecycle. Partial cleanup remains retryable; do not report
full deletion until both inventories are empty. Preserve the actual completion
time on retries. Legacy deleted records have unknown dates. A fresh lifecycle
clears deletion state; an expired/removed automatic preview is not recreated from
unchanged intent. A new head or explicit re-opt-in can create a new lifecycle.

Supersede ADR-0256's metadata-only expiry limitation for the Azure adapter. Keep
the portable request schema and existing provider primitives. Status distinguishes
no recorded deployment, stopped/preserved, cleanup pending and fully deleted.
Preserve-mode destroy reports stopped, not full deletion. A missing record plus
resources is an ownership error, never permission to adopt or erase them.

Acquire a separate exclusive capacity-blob lease before all Azure deployments,
then the existing environment lease. Hold capacity admission through provisioning;
cleanup never needs the capacity lease. Reserve non-deleted/preserved/incomplete
lifecycles and residual resources, with configurable defaults of two environments
and four vCPU. Count a same-environment update once. Unknown Azure namespaces
block admission. Infinite leases retain the existing interrupted-run safety
tradeoff: verified operator recovery is required after runner termination.

Inspect orphan candidates using both inventories and the existing private records.
Recover only resources with verified ownership. Stamp OS disk ownership only after
checking its owned VM and actual attachment, so detached future OS disks retain
proof. Never delete an untagged detached legacy disk by name alone. Report
unrecorded Azure/Cloudflare candidates for ownership recovery instead of inventing
a new inventory service or silently deleting unknown resources.

Use one bot-owned automatic feedback card per issue/PR, separate from command
history, and bounded public workflow results. Maintain `preview:status:deployed` as
derived verified running/protected status from command results and periodic live
checks, never as deployment permission. Remove it for unavailable/deleted or
unprotected previews without changing other labels. Track queued/missing-image/capacity
waits with `preview:status:pending` and operation/probe/health failures with
`preview:status:error`. Availability can coexist with waiting/failure of an update.
A healthy status read does not clear an unresolved operation error. Verified
deployment clears waiting/error and confirmed full removal clears all status
labels. Reuse bounded GitHub label transitions rather than adding Runtime state
or an event-history subsystem. Migrate the original derived label name while
preserving assignments. Do not export provider responses,
configuration, tokens, health logs or opt-in actor data. Retain main-only protected
environment, OIDC and read-only GHCR credentials. The manual form remains unchanged.

## Consequences

Expiry now causes full data/infrastructure removal, including previews created
before this change once their recorded expiry passes unless explicitly kept.
Main merge activates the
schedule; manual runs default to dry-run for qualification. Scheduled runs and
required environment approvals can delay cleanup; Azure budgets remain useful.
Existing cloud permissions suffice, including the existing disk write permission.
No new secret, broker, database or mandatory inference provider is introduced.
Post-merge live dry-run/apply, label lifecycle, quota and stale-event qualification
must be recorded before #1286 and its remaining native children are closed.
