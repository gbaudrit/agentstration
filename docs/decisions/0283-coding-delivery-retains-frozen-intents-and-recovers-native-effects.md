# ADR-0283: Coding delivery retains frozen intents and recovers native effects

## Status

Accepted — implementation for [#1349](https://github.com/gbaudrit/agentstration/issues/1349).

## Context

Existing issue coordination owns native acknowledgement, shared realization-plan allocation and
prerequisite admission (ADR-0276). Native Coding execution owns isolated AWP CLI/check evidence
(ADR-0279). Protected Git and GitHub connections own recoverable code/PR effects (ADRs 0264–0265).
Work owns functional evidence and portable usage (ADRs 0269 and 0272). A lost response between these
boundaries must not rerun the CLI, create another branch commit or create a second pull request.

## Decision

Add one explicit Coding delivery application service over those existing services. It consumes an
owned terminal atomic attempt, exact reviewed Project/plan/task/shared-plan versions, and an explicit
PR title/body. Server-side resolution validates durable Flow/AWP coordinates, pinned configuration,
captured Project connections, native issue acknowledgement/allocation and repository identity/base.
Completed execution requires a successful CLI and at least one actual configured successful check.
Failed and cancelled attempts retain and share failure evidence and usage without code or PR writes.

Work stores a private, immutable, bounded delivery intent and monotonic CAS receipt per functional
attempt in its existing SQLite/PostgreSQL stores. The intent freezes source digest, revision pins,
checks, bounded file changes, exact repository/base/head, reviewed PR content and publication identity.
It is persisted before external effects. Work storage remains independent of Coding/provider types.
Runtime source proof is read once at admission; an identical replay uses the retained intent.

The isolated helper seals its own Git index after checks and reads staged regular-file blobs through
Git inside isolation. It retains bounded UTF-8 file content, executable modes and deletions as an
optional native result field. It excludes materialized harness instructions/configuration and refuses
credential exposure. Unsupported binary/file-type/size capture leaves the generic execution outcome
intact with a separate publication diagnostic; delivery refuses an unpublishable completed result.
The server never applies a native patch to its filesystem.

A distinct, at-most-one Project `CodePublication` Tool binding authorizes the code branch and paths.
The Infrastructure adapter reuses protected Git resolution, current grants, exact relay/source pins,
Secret resolution, repository identity, literal ref/path restrictions and native write-ahead recovery.
The head is derived from plan/task/attempt under a bounded authorized prefix. A new ref uses the exact
captured base and create-only CAS; there is no automatic rebase or overwrite. PR creation uses the
existing GitHub gateway with both expected head and expected base SHAs and always creates a draft.

Persist each stage before its effects: evidence, code, PR, shared contribution, then shared usage.
Always recover first, including on the first request; execute only after a proven Missing result and
fresh source/base/allocation authorization. Reuse frozen operation keys and digests after restart.
Preserve successful code/PR receipts immutably. A changed base/review is actionable rejection;
uncertain effects and temporarily unavailable authority remain resumable. Existing provider journals
remain necessary; the local receipt is not a distributed transaction or a substitute for native CAS.

Publish an explicit bounded contribution containing portable issue/plan/task/revision/attempt/check,
code/PR and evidence references, followed by the retained usage records through existing sharing.
Exclude physical Workspace/private Project/actor, raw code, native output and credentials. An
independent authorized instance pulls progress and canonical usage with its own connections and store.
Technical delivery never accepts the functional task, merges the PR or closes the issue.

## Consequences

Authenticated REST begin/resume/read/list routes delegate to this service with separate read/execute
authority. Requests are bounded; each resume has a deadline and step budget. Larger bounded usage
histories can require another explicit resume. No scheduler, queue, distributed coordinator, new UI,
new MCP workflow, live model dependency or multi-repository delivery is introduced. Deterministic
fixtures exercise actual seed Bootstrap, native prerequisite admission, isolated AWP CLI/checks,
protected Git, synthetic GitHub HTTP, restarts and an independent reader. Live vendor validation
remains an explicit opt-in. SQLite is still the executable default.
