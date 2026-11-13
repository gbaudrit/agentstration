# ADR-0212: Source content has independent freshness and retention policies

## Status

Proposed

## Context

Feature [#1115](https://github.com/gbaudrit/agentstration/issues/1115) separates
acquisition freshness, optional input retention, and Knowledge Snapshot freshness
and history. Direct acquisition must not accidentally become retained content in
Flow state, runtime events, checkpoints, Tool arguments, or intermediate Artifacts.
History cleanup must preserve accepted executions, exact cursors, immutable
provenance, and the latest successful result after a failed replacement.

## Decision

A native Data Source may opt into an acquisition lifecycle. Its TTL starts at
successful completion. A retained input Storage is required for TTL or history.
Without input Storage, explicit `allowDirect` permits only the already governed
read-only Operation. Every new projection acquires again. The bounded direct
receipt contains a digest, size, expiry and exact projection request identity;
it never grants permission and cannot be reused by another projection.

Direct acquisition and all derived transformation/projection Flow Runs carry a
transient-payload marker. Existing SQLite and PostgreSQL persistence boundaries
retain redacted metadata plus opaque per-revision tickets. Payloads remain in a
bounded process-local handoff cache (15 minutes, 4,096 revisions, 64 MiB per
Workspace, 256 MiB per process). Direct result receipts expire after five minutes.
There are at most 32 active direct acquisitions per Workspace in the standalone
admission path. Capacity exhaustion fails explicitly; live entries are not evicted
to satisfy a new request. Per-revision tickets prevent a failed conditional write
from replacing a committed record's cached payload.

A cold process or expired receipt cannot resume direct content or silently replay
its Operation. Accepted work fails and the caller may explicitly start another
attempt. Intermediate Artifact capture is refused. Tool arguments and sensitive
errors are excluded from durable diagnostics. Ordinary composed child Flows inherit
the marker. Flow-backed Tool Definitions are refused in direct execution until
their WorkItem boundary supports transient payloads; they must not persist an
intermediate WorkItem as an accidental fallback. Only the final published Snapshot
materializes an Artifact for the resulting retained content.

Missing or expired retained acquisitions refresh under the Data Source policy.
A Knowledge binding may explicitly allow an older, compatible, currently readable
acquisition only after refresh fails. Its positive maximum age must be at least
the Data Source TTL. Evidence records both the failed refresh identity/code and
the exact selected old acquisition. Direct inputs never use this fallback.

Knowledge independently opts into a Snapshot TTL measured from publication. Current
native access prepares a new Snapshot when needed; an exact Snapshot or cursor
continues to identify its original content. Failed preparation preserves the old
active Snapshot. Serving it after expiry requires a separate explicit option.
`get_status` reports expiry, preparation need, and accepted input freshness without
triggering acquisition. Existing resources with no lifecycle retain their binding
refresh/max-age behavior; old dependency and Flow hashes remain readable.

Retention is independent of TTL. Without history, previous successes become
eligible only after successful replacement. With history, they become eligible
when their retention window ends. The latest current success and active Snapshot
remain protected. Mutable acquisition metadata records retirement. Immutable
Snapshot records remain unchanged; the existing per-source observed state records
retiring, retired, and provider-pending content.

Cleanup claims content with conditional writes before physical retirement. Native
input leases, running projections, and native read sessions protect exact bytes.
New native sessions check the claim before and after creating their lease; existing
cursors remain valid to their original expiry. New Artifact materialization checks
admission before and after submission. Conservatively, any nonterminal Workspace
Flow postpones cleanup, including already accepted Artifact materializations.
This can retain eligible bytes longer in a busy Workspace; it favors readers over
immediate space reclamation.

The existing acquisition recovery worker reconciles eligible histories every 30
seconds, with bounded inventories and per-pass work. Current actor, logical scope,
RunsExecute and resource/Storage Use are rechecked. Cleanup never acquires fresh
content and cannot undo successful publication.

Local retirement verifies the exact key, version, length and digest under the
provider lock. Remote ASP v1 has no generic delete: a provider advertising replace,
empty writes and ETag conditions may retire the verified value with a conditional
empty replacement. Providers without these capabilities remain pending. Provider
version backups are governed independently by their own retention settings.
Metadata, Artifact producer/Storage links and historical provenance are retained;
retired bytes are explicitly unavailable rather than silently substituted.

## Consequences

- Input and output Storage/history can be configured independently; Knowledge
  retained output still uses the supported built-in local publication provider.
- Direct work is process-affine and intentionally not durable across restart.
  The configured retention policy applies to Storage content, not to audit history.
- Conservative cleanup may be delayed by unrelated active Workspace work or by
  unavailable/revoked providers. No unsupported provider capability is invented.
- Deterministic fake-time lifecycle tests must keep runtime assignment/worker clocks
  independent from policy and Storage clocks, so advancing a TTL does not expire
  the test worker lease.

## Related decisions

- [ADR-0207](0207-knowledge-publishes-retained-storage-content.md)
- [ADR-0208](0208-produced-content-remains-an-artifact-materialized-in-storage.md)
- [ADR-0210](0210-knowledge-exposes-native-snapshot-access-tools.md)
- [ADR-0211](0211-resource-bound-flow-composition-points.md)
