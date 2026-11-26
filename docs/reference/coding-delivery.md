# Coding issue delivery

Delivery connects an existing acknowledged issue and allocated shared WorkPlan to retained native
Coding output, an authorized code commit, one draft pull request, shared progress and canonical usage.
See [ADR-0283](../decisions/0283-coding-delivery-retains-frozen-intents-and-recovers-native-effects.md).
This API increment adds no Console journey and never accepts a task, merges a PR or closes an issue.

## Prepare and execute

Initialize the local Project using a reviewed [Workspace seed](workspace-seeds.md) or the existing
resource APIs. Provide protected `Sharing`, `TaskOperations`, `Repository` and `CodePublication`
Tool bindings. Sharing and code publication should use separate refs so sharing history does not
advance the code base. The code relay uses the [Git capability](git-operations.md), a literal exact
repository URI and literal allowed ref/path prefixes, with the consumer's Secret credential binding.
It must allow `refs/heads/agentstration/` and only the intended code paths. Do not bind a fixed
`/request/refName` when using the derived delivery branch. Bindings pin current Tool UID/generation;
operator edits never silently substitute another publication.

Acknowledge an existing native issue through [issue coordination](repository-issue-coordination.md), export its
realization plan and allocate its tasks to the local participant. Native blocked-by dependencies must
be closed/completed; an upstream shared plan is a wait, not another locally dispatched task. Publish
and admit one [native Coding Flow](coding-worker.md) with exact harness, base commit and actual checks.
The optional Linux worker must have its exact local bare cache and approved check executable aliases.

Wait for the canonical functional attempt to become terminal. Delivery reads its durable final AWP
receipt and pinned Flow configuration. Completed attempts require CLI exit zero, at least one configured
check, every configured check completed with exit zero, and publishable file evidence. Caller-supplied
patches, repository identities, native receipts or result SHAs are not accepted.

## Begin, inspect and resume

Routes are relative to `/api/workspaces/{workspaceName}/projects/{projectId}/coding/deliveries`:

| Method and route | Contract |
| --- | --- |
| `POST /` | `id`, `planId`, `attemptId`, `expectedProjectVersion`, `expectedPlanVersion`, `expectedTaskVersion`, `expectedSharedPlanVersion`, `expectedSharedPlanDigest`, explicit `title` and `body` |
| `POST /{deliveryId}/resume` | Resume the retained intent; no replacement input |
| `GET /{deliveryId}` | Exact private operation and retained stage/effect receipts |
| `GET /?skip=0&take=25` | Owned Project page with `items` and explicit `hasMore`; no total count |

Writes require RunsExecute; reads require RunsRead and current Workspace/Principal ownership. Begin
accepts at most 32 KiB, title 512 characters and body 16,000 characters. GET pages allow `skip >= 0`,
`take` 1–100. Begin/resume return `202 Accepted` with a Location and explicit operation status: inspect
`Pending`, `Uncertain`, `Delivered`, `Rejected`, `Failed` or `Cancelled`. Expected validation/authority
failures use Problem Details. Another ID cannot admit another delivery for the same functional attempt;
reuse the original ID and exact reviewed request after response loss.

The immutable intent captures the reviewed issue/shared plan, source digest/coordinates, exact local
connections, changes/checks, target base and PR content. Evidence is retained before code publication.
The head is `refs/heads/agentstration/{plan:N}/{task:N}/{attempt:N}`. Code publication creates that ref
from the exact base; PR creation checks both base and published head and creates a draft. Each native
boundary recovers the same operation first. Changed base or review is explicit rejection, never an
automatic overwrite or rebase. Temporary denied/unavailable connections retain resumable uncertainty.
Successful receipts remain readable even if later publication fails.

A resume is bounded to three minutes and twelve stage steps. Repeat it for Pending/Uncertain operations
after resolving the reported condition. Do not invent a new ID to bypass uncertainty or lost provider
state. Native failures/cancellation publish their evidence and usage without a code commit or PR;
an explicit normal task retry gets its own attempt/history and delivery. A completed execution whose
publication is rejected requires explicit plan/task reconciliation; delivery never reruns it.

## Portable results and limits

The contribution records checks, source/plan/task pins, exact Git/PR receipts and `taskAccepted: false`.
Usage publication reuses the immutable [Work usage ledger](work-usage.md). Another instance can pull
both with its own participant, credentials and database; no conversation or Runtime database is copied.
Local physical ownership and raw code stay private.

Native code capture supports at most 32 regular UTF-8 files/deletions and a 32 KiB encoded change set
(24 KiB per blob). It preserves bytes, including BOM/newlines, and executable modes. Symlinks,
submodules, reserved paths, materialized harness files, oversized or non-UTF-8 content are excluded or
reported as unsupported; a completed result without publishable changes cannot deliver. Generic
execution evidence and usage remain retained. Existing sharing capacity limits still apply.

The offline end-to-end test uses real Git, Bootstrap and Bubblewrap with a deterministic fake CLI and
synthetic GitHub HTTP. Vendor credentials, service availability and live inference are separately opted
in; they are not requirements of the deterministic standalone path.
