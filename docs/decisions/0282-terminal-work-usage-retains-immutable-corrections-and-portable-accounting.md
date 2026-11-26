# ADR-0282: Terminal Work usage retains immutable corrections and portable accounting

## Status

Accepted — implementation for [#1348](https://github.com/gbaudrit/agentstration/issues/1348).

## Context

Flow Run accounting already owns nullable input, output and provider-reported total tokens,
complete/partial/unavailable coverage, and direct versus child-inclusive totals (#470).
Native Coding execution retains accepted AWP observations and final receipts, including failed
and cancelled attempts (ADR-0279). Project sharing supports immutable identified Usage records
and recoverable Git publication (ADR-0275). A portable plan needs accounting independent of a
particular instance's physical Workspace, private Project and Runtime database.

## Decision

Reuse the Flow canonical aggregation rules through a pure Flows domain helper. Never infer a
missing provider total from input/output or add cached/reasoning subsets to their parent counters.
Native `total_tokens` is retained only when actually reported. Multiple non-overlapping turn
reports belong to one identified native invocation; streaming, final-receipt and Flow-output
copies of the same observation are deduplicated, and conflicting copies fail capture.

Work owns an indexed immutable usage ledger in its existing SQLite/PostgreSQL stores. An explicit
terminal capture validates an owned historical functional attempt and its captured Project,
reads the existing durable Flow/AWP evidence through a narrow application port, and atomically
persists bounded originals before any shared publication. It does not add a Runtime projection,
queue, live counter stream or inference worker. Explicit recapture is idempotent after restart;
missing, expired, incomplete or oversized evidence is reported rather than treated as zero.
Previously admitted native attempts without reports expose unknown invocation cardinality.

Admission captures the portable sharing Project/participant pair with the existing Project context.
An older context without that pair remains local-only; editing today's connection cannot attribute
historical work to another producer. Physical Workspace, actor and private Project ownership remain
in the local envelope. Portable records retain plan/task revisions and digests, functional attempt,
root Flow Run, native assignment/attempt/invocation and bounded observation/event provenance.

Corrections append a new identified record with the original root, next revision, previous ID and
digest, explicit command identity, reason and evidence reference. The repository serializes competing
corrections and rejects forks. Identity, source observations and execution denominators remain fixed.
Measured, estimated and unknown quantities have separate rollups; estimates never improve measured
coverage. All source and correction records remain readable.

Sharing adds a typed accounting payload to the existing Usage document. Legacy counter-only records
keep their exact serialization and are reported as unsupported canonical accounting. New typed
records use an empty legacy counter map. Each immutable record uses the existing recoverable sharing
operation; corrections publish their ancestors first. Publication requires the captured portable
producer identity to match the current connection. A second instance reads the same accounting from
its authorized pulled sharing state without copying the first instance's database or executing Work.

A rollup validates complete, unforked lineages and selects the latest revision once per execution.
It explicitly chooses Flow direct or inclusive totals, task self or captured descendants, and a
set of plan IDs. Duplicate plan IDs count once; cross-plan waiting/dependencies never implicitly
charge another plan. A bounded complete read fails at the limit rather than summing a page prefix.

## Consequences

Terminal accounting is available through Workplace REST and the same Work application service used
by later orchestration. The API adds no Console/Workplace journey; display remains with the owning
UX issues. Capture and publication are explicit separate operations with retained receipts. No
pricing, billing, quota, live-provider dependency, historical backfill or Flow consolidation redesign
is introduced. Existing sharing limits still apply, including 64 retained records and 16 KiB content;
large histories must be reconciled explicitly rather than silently truncated or garbage-collected.
