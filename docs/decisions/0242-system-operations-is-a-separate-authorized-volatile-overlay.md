# ADR-0242: System operations is a separate authorized volatile overlay

## Status

Proposed

## Context

[#1200](https://github.com/gbaudrit/agentstration/issues/1200) needs operational
evidence without turning semantic topology into runtime state or execution
authority. Runtime and Flow already own durable runs and placement observations
(ADR-0108/0168). Historical Flow publications lack their owning resource UID;
a name/version join cannot prove association after resource recreation.

## Decision

SystemOperations.Contracts owns renderer-neutral queries, evidence and frames.
SystemOperations assembles the read model and ordered stream state. Infrastructure
adapts current authorization, family-owned identity queries, placement, provisioning
and exact WorkItem backlinks. SystemOperations.Api exposes GET /api/system-operations
and a private SignalR stream /hubs/system-operations, both requiring resources/read.

Each subscription selects one canonical graph node in the current server-derived
context. Store queries filter exact stored scope, explicit logical invocation
scope, native resource/revision UID, definition owner, namespace and requested
generation/publication/hash before limiting. Flow additionally preserves its
existing tenant/principal read restriction. Current active principal, run-read
access, membership and discovery are checked separately. Inspected dependency
evidence retains binding authorization. After collecting evidence the service
rechecks current authority and the same root graph slice before delivery.
Different graph slices have different observation digests.

New canonical Flow publications capture their owning Flow resource UID. Existing
publications stay immutable; missing evidence is not backfilled or inferred.
Direct Agent/AgentRevision Runtime runs and attested Flow runs provide direct
metrics. Separately, recorded Flow RuntimeBindings can associate a run with exact
Agent UID/revision/owner/namespace/generation/hash evidence. A binding alone does
not prove invocation and never contributes to direct-run counts, duration or tokens.
Native assignment ledger turns can additionally attest invocation coordinates:
exact run/participant, unique recorded binding, attempt, Worker session/fencing and
optional StepExecution. A step must match the stored run's exact version and
non-null execution hash. Unknown legacy coordinates and conflicting participant
bindings remain unattested; no name or reconstructed hash joins are used.
Unsupported measurements, families and unattested history remain unknown.
Flow binding queries preserve the same actor/tenant/logical/physical scope checks
before their limit. Target Agent namespace is independent of the consumer Flow's
namespace. Turn IDs are deduplicated before grouping by attempt/step; foreign
participants or sibling runs do not affect evidence. At most 100 binding associations
and eight invocation groups per association are delivered, with explicit truncation.
Assignment metadata is read directly through its existing store port after the
consumer run is authorized. The general Agent-instance observer resolves execution
material and is deliberately not called: observation creates no material/content lease.
Current Worker presence is reused only from the administrator-authorized inventory.
Consumer Flow state remains distinct from Agent invocation or Worker state.

A WorkItem identifier requires existing authorization, owner, exact scope and
current-execution backlink checks. No task join or Console WorkItem route is
invented; drill-down uses existing scoped Runtime and Flow run pages.

Samples contain active runs plus terminal runs whose storage update falls within
the chosen window (1–1440 minutes), at most 100 rows (Console: 15 minutes / 50).
These are observed counts, never global totals. Empty evidence has null metrics.
Mean duration is per completed observed run with valid timestamps. Direct Flow
tokens avoid double-counting inclusive child usage; missing or partial usage does
not become complete. Placement includes at most eight attempts per run. Current
worker presence is available only to current Platform administrators; other users
see unknown. Lease expiry, completion, attempt state and worker presence are
distinct facts. No global worker load/count or provider/run mutation is exposed.

Each private serialized stream emits snapshot, delta and heartbeat frames with
fresh epoch and monotonic sequence/base sequence. Bounds are one stream per
connection, one buffered frame, 64 global subscriptions and eight per actor.
Sampling waits five seconds after delivery, so slow clients do not queue ticks.
Disconnect, unavailable root, changed authority or source failure clears the client
overlay. A 30-second client frame deadline clears stalled evidence as stale, using TimeProvider. Duplicates are ignored; gaps or changed epochs require a fresh snapshot.
Console reconnects by toggling off/on, creating a new connection and epoch.
No broadcast group or durable operational cache is introduced.

The accessible Console panel and optional LikeC4 notes use the same evidence.
Localized notes are ephemeral presentation data; graph JSON, exported topology
and persistent layout remain unchanged. The renderer can be disabled independently.
Optional OpenTelemetry has bounded family/coverage dimensions; canonical identity
belongs only on the authorized snapshot trace, never metric labels.

## Consequences

- SQLite remains the offline default. PostgreSQL implements matching parameterized
  filters without a new schema/table; its live lane stays opt-in.
- Repeated reads are optimistic observation, not an atomic multi-store snapshot.
  Concurrent relevant change requires resynchronization.
- The initial cohort excludes inferred historic identities, step-level Agent
  duration/token aggregation, global fleet health and hierarchy-wide duration totals.
- Existing transports can retain physical Workspace coordinates for Instance/Tenant
  invocations alongside explicit logical scope. This differs from the eventual
  exact-scope direction of accepted ADR-0180. Both coordinates are exposed as
  stored; no migration or definition-owner substitution conceals the drift.
  Maintainer review is required for that architectural drift; ADR-0180 is unchanged.
- New reusable UX scenario #1203 remains deferred to explicit maintainer UX
  validation and a merged implementation PR. Core, adapter, HTTP/SignalR,
  component and temporary desktop/mobile checks remain required now.
