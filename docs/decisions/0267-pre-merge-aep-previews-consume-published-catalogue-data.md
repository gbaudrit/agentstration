# ADR-0267: Pre-merge AEP previews consume published catalogue data

Status: Proposed — 2026-11-26

## Context

[#1405](https://github.com/gbaudrit/agentstration/issues/1405) adds PR comment
commands, including the requested qualification on the still-open packaging
PR #1385. Its OpenAPI image is already privately published and qualified, but
ADR-0265's requirement for a catalogue entry in trusted main prevents testing a
new extension before merging its packaging. Merging the candidate first defeats
that preview use case. The actual command triggered run 45876181062, which also
exposed missing PR-conversation write permission and an uninformative GitHub API
error before any infrastructure execution.

## Decision

Keep scripts, provider adapters, the Compose recipe and protected credentials in
trusted main. Change only where deployment reads AEP catalogue **data**. This
supersedes ADR-0265's prerequisite that every selectable AEP entry already exists
in main; its fixed recipe and independently versioned delivery model remain.

Admission freezes syntactically valid, bounded, non-core catalogue IDs from labels
and rechecks them before execution. Published-image selection verifies that each
candidate package is private and belongs to this repository's fixed
`extension-<id>` image namespace. Freeze the index digest, verify its bytes and
Linux amd64 config, and read the catalogue at the exact source SHA recorded in
the image's OCI labels. Read the independent version only from a bounded
`src/extensions/<project>/version.txt` path at that SHA. Validate AEP identity,
protocol and contribution metadata with the existing receipt/image validator.
Do not execute or materialize its build files, project files or deployment code.
Existing main entries retain their identity checks; core server/Worker identities
and AWP compatibility remain unchanged. Independent newest-tagged extension
selection is unchanged: extension source may differ from the server's PR SHA.

Use existing version 2 public delivery receipts; no new request schema or mutable
catalogue cache is needed. The Azure transfer bundles minimal catalogue snapshots
constructed only from those canonical receipts, alongside trusted host scripts.
It does not copy raw candidate catalogue files or source/build instructions.
The same generic, isolated AEP container recipe works for pre-merge candidates.
A candidate cannot reuse another persisted extension's AEP identity, including
when that older extension is deselected. Keys and volumes retain their existing
lifecycle and explicit reset semantics.

Missing/unreadable/public packages, missing publication, malformed metadata,
namespace changes and unsupported protocols fail before provisioning. Unknown IDs
are not silently skipped. Saved status/destroy continue without registry or
catalogue lookups. Fork deployment, arbitrary Compose, host scripts and expanded
cloud permissions remain unsupported.

Feedback writers and their reusable caller request `pull-requests: write` as well
as issue write permission; ownership-only checks retain read access. GitHub API
failures name a fixed operation stage without reproducing remote bodies or
comment contents. The main-only provider boundary is unchanged.

## Consequences

New extension packaging can be tested before merge using its already published
private OCI image. Private publication and validated, frozen image-source
metadata become the admission gate for a new AEP identity; registration in main
is no longer that gate. This is limited to the existing AEP recipe and image
namespace, and does not authorize candidate-controlled deployment logic.
Selectable labels may be provisioned before the packaging merge; the main label
synchronizer continues deriving its regular catalogue after merge.

The server image must still exist for the PR's exact head SHA. A comment does not
build images. Actual human-comment dispatch, protected Azure enrollment/readiness,
update and full cleanup must still be qualified after this tooling merges.
