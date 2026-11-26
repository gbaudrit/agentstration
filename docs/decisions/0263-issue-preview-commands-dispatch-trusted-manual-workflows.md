# ADR-0263: Issue preview commands dispatch trusted manual workflows

Status: Proposed — 2026-11-26

## Context

[#1382](https://github.com/gbaudrit/agentstration/issues/1382), a child of #1286,
adds issue comment commands to the qualified manual preview vertical. An issue
has no implicit source revision. GitHub comment events may be authored by anyone,
while Azure/Cloudflare execution must retain its trusted-main boundary. The
existing manual dispatch form already uses GitHub's ten-input limit.

## Decision

Keep commands outside Runtime. A credential-free issue-comment workflow accepts
only newly created ordinary-issue commands on default `main`, checks the live
comment and current repository write permission, and parses bounded data without
shell evaluation. Discover at most 50 native linked open closing PRs. Select
exactly one same-repository head, or require an explicit reference when none or
several exist; refuse fork PRs. Resolve branch/tag/PR to one SHA before dispatch.

Dispatch a dedicated one-input main workflow that calls the existing manual
workflow with canonical form options and issue/comment correlation. Preserve the
original ten-input manual form. Reuse its published private image selector,
resolved environment defaults, canonical request and owned provider lifecycle.
Never execute deployment tooling from the selected application revision. Keep
the provider's workflow-dispatch/push main-only event guard unchanged.

The receiver rechecks the live original comment, permission, issue eligibility
and stable `issue-N` identity. It repeats authorization before publishing the
frozen selection. A successful issue announcement of SHA/digests/run link gates
infrastructure execution; failure stops it. Report only a trusted successful
lifecycle result's protected URL, or safe failure/run-link feedback. Do not
consume untrusted workflow artifacts to decide deployment or publish URLs.

Use explicit secret inheritance through reusable callers. Admission has only
GitHub metadata/comment/dispatch permissions and no cloud environment/OIDC.
Announcement/result jobs have comment permissions without cloud credentials;
the existing executor retains its protected scope. Serialize manual and issue
operations by the same environment identity without cancellation.

Issue deploy defaults to SQLite, size slot 1, two recorded TTL hours and the
independently published Worker. Status/destroy reuse recorded state. The explicit
issue `destroy` command means full reset, including disks/IP/data; the manual form
continues to offer preserve mode. The issue must be open for deployment, while
closed issues can still inspect/remove their environment.

## Consequences

Maintainers can deploy from an issue without typing its single linked PR number,
with the same digest and ownership guarantees as the manual form. Linked PR
discovery uses GitHub's closing relationship, not arbitrary textual mentions.
Comments/links never authorize an unbuilt image, fork, foreign target or changed
command. No PAT, deployment service or parallel lifecycle inventory is added.

An extra dispatch wrapper preserves form compatibility and requires explicit
transitive secret inheritance. GitHub API/comment failures can prevent a deploy;
the manual form remains the recovery path. Workflows require repository policy
to permit Actions/issue writes. Automatic TTL/PR lifecycle, global cost quotas
and orphan reconciliation remain separate #1286 verticals. Offline contracts and
workflow lint precede merge; actual Actions permissions, secret propagation and
issue feedback require trusted-main post-merge qualification.
