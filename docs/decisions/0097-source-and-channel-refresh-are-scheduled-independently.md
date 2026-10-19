# ADR-0097 — Source and Channel refresh are scheduled independently

## Status

Accepted

## Context

A Source definition and the content selected by one of its mutable Channels change independently. Definition retrieval is an HTTP manifest operation that can create a new immutable Source Version. Channel refresh resolves and materializes provider content for one already imported version. Combining both operations would couple their cadence, cost, failures, and provenance, while Registry catalogue refresh is a third concern owned by the Registry feature.

## Decision

- Mutable local refresh configuration is stored with `SourceConfiguration`, outside immutable `Source` and `SourceVersion` resources. Source-definition and default Channel policies are independent, and exact Channel names may override or disable the default.
- Policies are disabled by default. The first increment supports bounded intervals, a minimum interval, deterministic jitter, timeout, limited exponential retry/backoff, and no cron syntax.
- A URL import retains its explicit HTTP(S) origin. Later definition refresh uses conditional ETag and Last-Modified validators; an HTTP 304 records an unchanged successful attempt without creating a Source Version.
- Manual and scheduled operations reuse the same Source and Channel services and their keyed concurrency controls. Persisted observed states hold the last trigger, outcome, error, and consecutive failure count so due/backoff decisions survive process restart.
- The hosted scheduler uses `TimeProvider`, scans every 30 seconds in system context, and remains inactive for disabled policies. Caller shutdown cancellation does not publish failure; a policy timeout is an observable scheduled failure.
- Scheduled Channels are evaluated against the running Agentstration version before refresh. Incompatible or unknown Channels are recorded as skipped with an actionable reason, retain their last usable snapshot, and are reconsidered at their next interval.
- Importing a new Source Version does not automatically materialize Channels. Removed Channel overrides remain harmless local configuration and are not executed unless the exact Channel exists in the current version.
- Registry discovery, cache refresh, trust, and provenance resolution remain outside this scheduler. Registry work may reuse its timing patterns but cannot implicitly import Sources or schedule Channels.

## Consequences

Administrators can tune inexpensive definition checks separately from provider materialization and can disable costly Channels. The local executable remains offline by default. Policies share the Source configuration ETag with display name and binding changes, providing one optimistic-concurrency boundary for all mutable Source settings. Scheduling is process-local, so only the current modular-monolith deployment executes it; distributed leasing remains outside this increment.
