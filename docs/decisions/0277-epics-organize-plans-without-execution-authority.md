# ADR-0277: Epics organize plans without execution authority

- Status: Accepted — implementation for #1364
- Date: 2026-11-25
- Related: ADR-0266, ADR-0274, ADR-0276, #929, #1346, #1211

## Context

One tracking repository already represents the project. Authors need optional logical epics, not
another Project directory, GitHub parent issue, composite task or dependency scheduler. Organization
must remain independent of plan revisions, active execution evidence and accounting identity.

## Decision

A Work organization definition contains bounded epics with stable portable IDs, display content and
explicit plan membership. One plan has at most one epic and may be ungrouped. Folder names are
organizational; moving a file does not create or move functional Work identities. An epic has no
issue/Flow reference, execution state, completion aggregation or implied dependency.

The private organization snapshot is stored in the existing Work tracking-document store under its
own revision and durable command receipts. Organization writes validate exact Project membership
and scope, but never save a Project/plan/task or retarget an execution. A private storage CAS version
retains replay receipts even for no-op commands; the public organization revision changes only when
its normalized content changes. Stale/foreign/dangling or duplicate membership is rejected.

`data/catalog.yaml` may explicitly reference WorkEpic YAML manifests. Omission/null preserves the
current organization; an explicit empty list clears grouping. A reviewed Work import freezes the
organization selection/version, prepares selected drafts, then applies organization through its
own service. A concurrent local organization edit yields an explicit partial conflict requiring
review; no group removal deletes plans. Pure organization imports may select zero plans.

Sharing format 2.0 adds an independent `Organization/epics` record. It contains organizational
definitions only, and references existing shared plan records. Publication guards the current local
organization; pull retains a projection and explicit reviewed adoption updates local organization.
Neither publication nor adoption changes a shared Plan record/digest or writes issue relationships.
Uncertain publications use the ordinary retained sharing-operation recovery path.

## Consequences

Grouped and ungrouped Work stay usable without a GitHub/GitLab account. Shared and local grouping
changes do not invalidate unchanged functional execution evidence or duplicate usage. This Work
organization contract does not introduce the managed-resource classification lifecycle in #1211.
The API/document slice adds no browser journey; any future epic editor requires an explicit owning
UX feature and its validation follow-up.
