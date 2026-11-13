# ADR-0215: Process regions have derived Flow identities

## Status

Proposed

## Context

[#1162](https://github.com/gbaudrit/agentstration/issues/1162) generalizes the
private processing execution introduced by ADR-0213. Its runs currently reuse the
parent Flow identity. A named sub-Flow can inherit Artifact access, so that access
context cannot identify an implicit process run. Operators also need to distinguish
an originating failure from the same failure propagated through enclosing runs.

## Decision

A process-capable step exposes a named region in its immutable graph. The authority
extracts that region and records a derived definition origin: source Flow address,
version, snapshot/hash, owner step, region key and extracted graph hash. All units
of that region reuse this definition identity. Existing FlowId/FlowVersion fields
remain the source reference for compatible readers; ProcessOrigin explicitly
identifies the executable definition and prevents treating it as a published Flow.
No catalog resource or independently published version is created.

Each admitted unit is an ordinary child Flow Run, with a stable key and ordinal,
its exact owning run/step attempt and the authenticated AWP StepExecution identity.
The Worker supplies bounded keys and descriptors, never a graph or origin. The
authority derives the region from the accepted snapshot, rejects conflicting replay
and new admissions after explicit closure, and attaches admitted children immediately
to the owning step. Empty admission is explicitly closed. A producer may declare a
fixed total; otherwise the total remains unknown until closure. Unit output contracts and aggregate step output contracts remain separate.
Artifact capabilities remain isolated by their existing owner/attempt/index keys.

The initial general-purpose executor is optional processing on a Transform step:
array batches or bounded text chunks demonstrate executor-owned partitioning without
requiring Artifacts. This is an executor capability, not a restriction on the generic
admission model. Artifact transformation uses the same region/admission mechanism.
Each executor owns stable ordering and aggregation. The Artifact executor prepares
all results before publishing; Transform aggregates in input-unit order.

Process chains execute on their root's assigned Worker under the existing fenced
AWP ownership (ADR-0168). Per-owner parallelism is bounded; a shared assignment
budget bounds active process lanes. Nested processing reuses its enclosing lane
sequentially, avoiding capacity starvation from parents waiting for their children.
Children are not separately offered for placement, and no new broker/Job service is
introduced. Root descendant reservations use optimistic persistence to prevent
concurrent admissions exceeding the configured bound. Deadlines and cancellation
continue through the root assignment. Cancellation terminalizes unfinished children.

Admission/replay reuses terminal units without repeating their side effects. A changed
unit input/key/ordinal or definition is refused. Interrupted units can be retried by
existing assignment recovery; external non-idempotent side effects are not exactly
once. Unit attempts and existing Tool idempotency coordinates remain inspectable.
A new parent step attempt creates new unit runs rather than mutating old evidence.

Failure references retain the originating run, step and process unit. Propagating a
failure preserves that reference, while aggregation/contract failures acquire their
own origin. Console inspection links to that origin in the exact execution scope.
Inclusive token totals are never summed across parent and child rows; elapsed time
continues to use each run's own start/end interval.

New metadata is additive inside existing run documents in SQLite/PostgreSQL, so no
relational schema change is required. Old run URLs and snapshots remain unchanged.
Legacy Artifact children may resume only after verifying their deterministic identity,
exact graph/input, scope and processing coordinates; their evidence is not relabeled.
Absent origin means legacy/unspecified; do not infer process origin from a name,
timestamp or inherited Artifact context. Genuine named sub-Flows have no process
origin even when they inherit Artifact capabilities. Existing artifactProcessing
AWP admission remains compatible and receives authority-derived metadata.

Projection launch returns after durable admission and executes through the existing
recoverable projection processing path. The Console follows persisted status and
shows the terminal execution error; a transport timeout is not execution failure.

## Consequences

The Console distinguishes process units from named invocations and groups them by
owning step in causal inspection, with bounded input/output disclosures and scoped
links. Default concurrency remains one; authors opt into parallelism. Exact-scope
isolation, immutable snapshots, transient payload protection, AWP fencing and bounded
descendant/query limits remain mandatory. The new reusable browser journey is
tracked by #1163 and retains its UX-validation/merge gate; existing regressions and
smoke checks are required during implementation.

New root graphs containing process regions, named/repeated child calls or Artifact
capture use execution-material schema `1.1`. Current Workers advertise `1.0` and
`1.1`; a `1.0`-only Worker cannot claim these new assignments. Existing `1.0`
assignments keep their immutable references and legacy terminal-child response shape.
Resolving a new generic process or explicitly parallel Artifact process through an
old assignment fails explicitly instead of silently treating it as an ordinary
mapping. Start a new run with an updated Worker in that case. Wire protocol and
Runtime capability versions remain `1.0`; these are separate version dimensions.
