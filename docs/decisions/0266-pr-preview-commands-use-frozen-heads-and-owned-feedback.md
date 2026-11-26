# ADR-0266: PR preview commands use frozen heads and owned feedback

Status: Proposed — 2026-11-26

## Context

[#1405](https://github.com/gbaudrit/agentstration/issues/1405), a native child of
#1286, extends ADR-0263's issue commands. A `/preview deploy` comment on PR #1385
was skipped by the explicit ordinary-issue guard. Maintainers also need feedback
that explains availability and recovery, rather than successive metadata dumps.
ADR-0265 already freezes independently published extension selections from labels.

## Decision

Accept newly created `issue_comment` commands on both ordinary issues and PRs.
Run admission and all deployment tooling from trusted `main`; never check out or
execute the selected PR's scripts. Preserve live write/maintain/admin permission,
unedited command checks, the main-only dispatch relay, protected environment and
provider event boundary. There is no `pull_request_target` deployment path.

On a PR, deploy its own exact head SHA from the same live response that verifies
its open state and same-repository base/head. Ref overrides may only repeat that
PR's identity; use an issue or manual form to select another revision. Read the
PR's own deployment labels, and recheck ownership, open state, permission, command
and labels before execution. A later push does not substitute the admitted SHA;
a new command freezes the new head. Fork PR deployment remains unsupported.

Use stable `pr-N` identity and `dev-pr-N` hostname. Ordinary issues retain `issue-N`
and native linked-PR discovery; the ten-input manual form is unchanged. Repeated
deploy preserves data. Status/full-reset destroy use saved state and do not need
an open PR, surviving head repository, current catalogue or registry lookup.

Create one bot-owned feedback card per admitted command before dispatch. Bind it
to the complete canonical selection with a SHA-256 marker. Later stages verify
its issue/PR, bot login/type, ID and marker before patching that card. Never patch
the original user command or another bot's comment. Keep legacy in-flight issue
selections without card identifiers compatible. A new command gets its own card,
so concurrent command histories cannot replace each other's source selection.

Render French operator feedback: requested/checked/completed state, short linked
server commit, database and selected extensions, VM/region when known, verified
protected URL, expiry limitation, relevant lifecycle commands and workflow link.
Keep full component versions/digests in collapsed details. Missing image/private
package failures carry only controlled component IDs and error codes; failed
workflow stages provide bounded recovery guidance. Do not copy provider exception
bodies, private settings or credentials into comments. Boundary failures may update
the already authenticated card, without relaxing the infrastructure gate.

## Consequences

PRs no longer need a separate tracking issue. No new cloud infrastructure, PAT,
Runtime dependency, public registry or mandatory inference service is introduced.
Published images are still prerequisites; the command does not build them.
Deployment remains explicit on updates and label changes. Automatic expiry
cleanup is still #1286 work; comments state this limitation instead of promising
an automatic stop. Event delivery and bot-token comment updates require actual
post-merge qualification on trusted main, in addition to offline boundary tests.
