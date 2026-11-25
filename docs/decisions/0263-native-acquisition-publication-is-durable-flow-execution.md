# ADR-0263: Native acquisition publication is durable Flow execution

## Status

Proposed

## Context

[#1369](https://github.com/gbaudrit/agentstration/issues/1369) completes the execution
boundary described by ADR-0204, ADR-0208, ADR-0211 and ADR-0235. Previously the
operation/transformation Flow succeeded before recovery or consultation prepared,
sealed and registered its output. A successful Flow therefore did not prove a
usable acquisition. PR #1218 separates native history reads from that work and
shares AEP discovery inside an operation. Both corrections are retained here.

## Decision

New retained native acquisitions using the standard orchestration select immutable
`data-source-orchestration-builtin@2.0.0`. The graph contains the operation
composition point, mixed Function/Flow transformations, then three ordinary governed
Tool steps: `publication-prepare`, `publication-seal` and `publication-register`.
These execute under the root AWP assignment through the existing Flow Tool pipeline.
They are internal capabilities, not a new public Tool exposure requirement.

The Tools accept only the acquisition identity. The server checks caller, physical
Workspace, logical scope, root run and exact step, and reads the completed
`transformations` output from the authoritative Flow store. It rechecks current
execution and dependency permissions at each phase. No Worker-supplied destination,
record body, binding token or credential determines publication authority.

Preparation persists its deadline before writing and its immutable membership and
record contract before sealing. Sealing persists separate confirmation before
registration. Registration reuses `dataset:{acquisitionUid}` in the existing Artifact
catalog and persists the manifest before the Flow can complete. Provider writes and
control-plane recording are separate commits: replay after a lost acknowledgement
uses the same publication identity and conditional/idempotent existing Storage ports;
a lost registration acknowledgement uses the same Artifact identity. No distributed
transaction or exactly-once network transport is claimed. Failed/cancelled output
retains preparation/sealing evidence and is never a successful acquisition.

An explicit retry after a failed publication resumes the same acquisition/root and
successful checkpoints. A functional error output completes the assignment transport;
the existing fenced requeue path starts its next attempt. Source/transform failures
retain the existing new-acquisition retry behavior. Cancellation/timeout never resumes
a publication automatically. A crash between recording the resumed run and enqueueing
is recovered through ordinary Pending Flow recovery.

Normal graph progression executes publication immediately. The existing bounded
assignment, ownership proof, fencing and cancellation remain authoritative. Transient
Storage 503/504 failures retain a nonterminal step with a persisted provider wait and
bounded attempts under the existing execution deadline. Recovery scanning remains a
safety path, not the normal publication trigger. No queue, broker, secondary executor
or general Worker scheduling change is introduced.

AWP execution material **1.2** supplies successful graph-step checkpoints (outputs and
selected transitions) from the existing Flow store. Updated Workers restore them
without reopening successful steps. Incomplete child calls retain existing
deterministic child identities. New durable standard roots require 1.2; older Workers
cannot claim them. Historical 1.0/1.1 assignments keep their original material and
behavior. Direct results do not gain Storage steps. Root and children execute within
one assignment, without reserving another Worker slot. The existing nested Agent Tool
capacity limitation [#1029](https://github.com/gbaudrit/agentstration/issues/1029)
remains separate; an Agent retaining its own assignment can still require spare
capacity for a newly submitted root.

`FlowStepRun.OperationProgress` records active/provider-wait state, attempt, timestamp
and safe error code. `StepProgressChanged` events retain the transition history.
Metadata-only acquisition projections select the current persisted Flow step and
expose the same progress to Console/API consumers. Actual step outcomes, start/end,
children and governed Tool evidence remain in Flow Run details. No record percentage
or processed count is inferred from a batch call. Terminal assignment projection
records the acquisition outcome; periodic recovery reconciles a lost final projection.
GET/list/render/refresh never prepare, seal, register or repair native acquisitions.

## Interfaces shared with #1373

- `DataSourceAcquisitionResource.Name`/`Uid` identify the accepted production
  acquisition; `FlowRunId` remains its global/root execution identity.
- `Dataset.OperationFlow`, `Dataset.Flow` and `Dataset.Composition` retain exact
  operation/root/version/hash and ordered Function/Flow pins. `DurablePublication`
  is accepted server evidence, not an editable transformation setting.
- `DataSourceAcquisitionProgress` supplies `StepName`, invariant `State`, `Attempt`,
  available start/update/completion timestamps and safe `ErrorCode`.
- Step identities `publication-prepare`, `publication-seal`, `publication-register`
  are execution coordinates. The existing Flow child identities and links remain
  authoritative; a preview/sample identity must never stand in for a production one.
- Operation contracts, adopted native-content contracts, sequence editing, raw samples
  and preview/replay remain owned by #1373. This change does not modify their editor.

## Compatibility and consequences

Published 1.0.0 is preserved. Already accepted acquisitions use their stored pins and
original finalization path; steps are never inserted into them. Historical Profile,
custom orchestration and retained native histories remain readable. Explicit custom
or old-version orchestration retains its existing completion semantics; adopting the
new standard version is an explicit reference/admission decision. No data reset or
immutable rewrite occurs. Direct native completion records its bounded ephemeral
receipt on terminal execution, without publishing intermediate content. Existing
Knowledge consumers still wait for the acquisition outcome and use its unchanged
one-Artifact/many-record receipt and exact content authorization.

The new fields use existing JSON resource/Flow persistence on SQLite and PostgreSQL;
there is no relational table migration. Deploy the updated Worker with the server to
execute new 1.2 roots. Older accepted executions retain their version requirements.

Deferred reusable UX scenario [#1370](https://github.com/gbaudrit/agentstration/issues/1370)
remains gated on explicit maintainer validation of the implemented UX and merge.
Implementation behavior, regression and desktop/mobile smoke checks remain required.
