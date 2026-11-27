# ADR-0271: Opted-in PRs publish server images through main

Status: Proposed — 2026-11-27. Owning feature: [#1286](https://github.com/gbaudrit/agentstration/issues/1286).

## Context

Preview reconciliation waits for exact private images but previously required a
separate manual `publish_preview=true` run after each PR update. Automatic
deployment should also prepare its server image without giving PR source code
registry-write or cloud credentials. Worker and AEP images retain independent
release cycles.

## Decision

Use `pull_request_target` only for metadata admission from trusted main on PR
updates and the `preview:auto` label event. Reuse reconciliation's live human
write-access grant, same-repository, open and non-draft checks. Ignore obsolete
webhook heads and unrelated label changes. A manual PR-number admission supports
existing opt-ins and retries. Dispatch the existing OCI workflow on main with the
fixed server component, exact source SHA and private-preview publication enabled.
This is not a PR-event deployment executor; ADR-0269's cloud boundary is unchanged.

Before building, recheck live approval and verify whether that exact server image
already exists privately. Reuse verified publications. Only definite missing
manifests permit a new build; access failures do not permit fallback. Keep the
existing OCI workflow, archive validation, database/guest smoke checks and
immutable publication behavior. Serialize duplicate requests for one PR/head;
different heads/PRs have separate concurrency groups so GitHub's single pending
slot cannot discard another preview's publication.

The reusable builder may check out the approved candidate SHA with contents-read
permission only. It has no registry-write token, inherited secrets, protected
environment or cloud identity. Candidate code, build recipes and tests execute
only there. A separate publisher checks out main's workflow commit, consumes the
same-run archive as data, and never imports or executes candidate scripts. Verify
receipt size, exact repository/SHA, supported source catalogue/version, local
archive path/checksum, OCI index digest and image configuration identity. Recheck
the live opt-in grant/head immediately before copying. The only destination is
the pre-provisioned private server package at `sha-<source>`; its immutable digest
cannot be replaced. Recheck package privacy after copying. There is no automatic
Docker Hub or Worker/AEP publication.

Successful OCI completion wakes the existing main-only reconciler, which resolves
current PR intent and private digests independently, without consuming candidate
artifacts or trusting an upstream claimed deployment. Packaging failures update
the existing bot-owned automatic card and error label, preserving last verified
availability. Stale failure results cannot overwrite a newer head's feedback.

## Consequences

No new PAT, Azure permission, Runtime component or protected configuration is
required. Existing repository Actions Write access on the private server package
is required. Worker/extensions must already be published through their own flows.
Existing manual/tag packaging remains available. Prior opt-ins need one manual
admission or a new push/label event after merge. Approval removal can stop
publication until the final check; a concurrent removal after that check may leave
an unused private image, but deployment independently rechecks authorization.
Live automatic publication must be qualified from main after merge.
