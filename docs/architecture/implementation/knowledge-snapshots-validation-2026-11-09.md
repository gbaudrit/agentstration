# Retained Knowledge Snapshot implementation and validation

## Scope

This report covers [#1042](https://github.com/gbaudrit/agentstration/issues/1042)
and [PR #1088](https://github.com/gbaudrit/agentstration/pull/1088), compared with
`integration/fr-1032` at `f27857701517e0ac2fa2561ae180d7eb2c680585`.
The baseline includes the merged Data Source composition in #1084. Parallel ASP
Sessions (#1086) and OpenDAL (#1082) are not prerequisites or qualified Snapshot
providers. The implemented publication decision is
[ADR-0207](../../decisions/0207-knowledge-publishes-retained-storage-content.md);
accepted ADRs are preserved.

## Delivered verticals

- **Composition:** extend the existing Knowledge definition, services, endpoints
  and editor with exact output Storage/rule selection, native dataset pins,
  per-binding refresh and explicit adoption of dependency revisions. Disabled
  native preparations remain editable. No second composition mechanism is added.
- **Projection and publication:** reuse acquisition reading, optional
  transformation and aggregate projection Flows through Runtime/AWP. Storage owns
  preparation, sealing and exact retained reads. Persist accepted request and
  input identities; separately create and activate immutable Snapshots. Idempotent
  retries, bounded admission, activation fencing and the existing recovery worker
  preserve accepted work and provenance across host replacement.
- **Freshness and retrieval:** implement all four refresh/maximum-age combinations
  using successful completion and `TimeProvider`. Validate explicit acquisitions;
  required failures stop publication and optional failures remain visible in
  provenance. Retrieval checks the persisted Snapshot and complete content
  evidence, reauthorizes access and reads the recorded version.

The Console retains creation/editing, projection history, Snapshot history and
retrieval tabs, with neutral/French labels and retained output references. Legacy
Artifact-only definitions and Snapshots retain their receipts and read path.
There is no destructive migration or history reset.

## Executed functional scenario

`DataSourceDatasetTests.NativeKnowledgeProjectsTwoInputsThroughAwpIntoAnIndependentRetainedStorage`
runs four policy variants. It creates two native Data Sources, executes their
acquisitions into local input Storage, executes a transformation and the aggregate
projection through the real AWP worker, and publishes two outputs into a separately
selected local Storage. Retrieval searches the exact retained publication.

The scenario also verifies concurrent same-key retries, conflicting key reuse,
shared inputs across two Knowledge Sources, different publications, old content
preservation, rejected content substitution, failure preserving the active
Snapshot, disabled-source retrieval refusal, explicit dependency rebinding,
changed Storage revisions and logical key bounds. A separate test exercises
four-byte Storage read limits without widening the envelope.

The actual-host-restart test uses persistent SQLite and filesystem content. A
decorator interrupts the real output publisher after sealing; retry resumes the
same identity. It then records interrupted activation/completion, disposes the
host, creates another host and recovers/retrieves the same publication without
another origin acquisition. This validates the executed local path; it does not
claim a distributed transaction or protection against filesystem loss.

## Completed validation

Checks were run on 2026-11-09 using .NET SDK 10.0.300 and deterministic offline
fixtures. Required test minima and module-concurrency limits were preserved.

| Check | Result |
| --- | --- |
| Release solution build | Passed; zero warnings and errors |
| Fast solution, minimum 777, maximum 4 modules | 1,124 passed; zero failures or skips |
| Integration solution, minimum 954, maximum 2 modules | 1,149 total: 1,137 passed, 12 skipped, zero failures |
| Focused native dataset/Knowledge module cases | 12 passed |
| Temporary editor browser smoke | Four passed: desktop/mobile × en-US/fr-FR |
| Existing governed Knowledge browser smoke | Three passed: desktop, mobile and French |
| Existing Knowledge projection lifecycle after selector correction | Three passed: desktop, mobile and French |
| Hosted browser CI on executable commit `ed78496` | Four campaigns and aggregate check passed: both desktop shards, mobile and locale contracts |
| TypeScript checking | Passed |
| ADR registry | 203 unique indexed records; passed |
| LikeC4 validation/generation and complete Docusaurus build | Passed |
| Changed-file formatting and whitespace checks | Passed |

The Integration skips are the two optional PostgreSQL cases and ten optional live
model cases (eight Foundry combinations, llama.cpp and LocalAI). They do not
replace deterministic integration coverage.

Local browser checks use installed Chromium because the environment cannot
download the Playwright browser. Video is disabled locally because its bundled
ffmpeg is unavailable; screenshots and traces are retained. Editor checks verify
interactivity, saved selection and absence of horizontal overflow at desktop and
mobile widths. Temporary implementation checks are not a new reusable journey.

Initial hosted browser runs failed in the existing Knowledge lifecycle. Local
reproduction identified selectors made ambiguous by the new help text and refresh
checkbox. The existing page object now selects Enabled and Required input by their
localized accessible names. The complete projection lifecycle then passed in all
three browser variants. An expanded desktop reproduction started before that
correction finished with 33 passes and the one known selector failure. The expanded
mobile/locale reproduction finished with 41 passes and that same cached mobile
selector failure. These failed runs remain recorded. The [final hosted browser run](https://github.com/gbaudrit/agentstration/actions/runs/42657538055)
passed all four campaigns and the aggregate check on executable commit `ed78496`.
The subsequent delivery commit contains documentation only.

## Supported guarantees and remaining gates

- Retained publication supports the builtin local Storage implementation only.
  Sealed content occupies a separate namespace outside mutable ASP objects;
  publication retries compare evidence and cannot overwrite sealed documents.
- Each content object is bounded to 64 KiB, with lower Storage envelope limits
  preserved. Capacity is 1,024 retained documents per Storage/execution Workspace.
  Admission is bounded to 32 executing or queued projection calls per process.
- Old unsealed leftovers are cleaned in bounded batches. Sealed publications have
  no deletion API or automatic garbage collection. Operators must persist both
  control-plane data and the configured local content root.
- A changed or revoked dependency fails explicitly. Historical references are
  never redirected to a newer Storage revision or backend; retained bytes remain
  intact even when the old revision becomes unavailable.
- Generic ASP read/write capability, OpenDAL and ASP Sessions do not establish
  retention guarantees. Other providers require a real publication adapter.
- [#1033](https://github.com/gbaudrit/agentstration/issues/1033) still owns the
  reusable integrated browser journey, blocked by explicit implemented-UX approval
  and required implementation merges. No UX approval, merge or issue closure is
  claimed by this report.
