# ADR-0101 — Source registry refresh joins the shared local scheduling lifecycle

## Status

Accepted

## Context

Source and Channel refresh already use one process-local worker, deterministic scheduling, persisted observed state, finite timeouts, and bounded retry/backoff. Registry registrations add a third refresh target with different retrieval and cache semantics, but a second worker or scheduling framework would create competing clocks, retry rules, and concurrency behavior.

## Decision

- The existing Source refresh worker and `SourceRefreshScheduler` also scan enabled Registry registrations whose periodic policy is explicitly enabled. Registry scheduling does not introduce another hosted worker or distributed lease.
- Registry policies use the same interval, deterministic jitter, finite timeout, maximum-attempt, and exponential-backoff model. Last attempt and consecutive failures are persisted so due decisions recover after restart.
- Manual and scheduled Registry refreshes enter `SourceRegistryManagementService`. Refresh, registration update, disable, and delete share one coordination gate; a waiting scheduled attempt revalidates its observed-state timestamp before retrieval.
- Conditional `ETag` and `Last-Modified` validation remains owned by the Registry service. HTTP 304 is successful, failures retain the last-known-good observation, and a later success records a recovered state before returning to the normal lifecycle.
- Freshness is derived from the persisted last successful validation and the registration's `staleAfter` policy. Cache cleanup runs only after a new observation is current, retains the configured number of complete observations, never removes the current observation, and preserves immutable refresh history.
- Refresh history is the operational audit trail for the registration UID, trigger, result, duration, retry count, correlation, digest, and actionable error. Structured logs and metrics expose the same non-secret operational dimensions. Registry documents and credentials are not logged.

## Consequences

Registry scheduling survives local process restart and remains offline by default. Operators get one refresh loop and consistent retry semantics across Sources, Channels, and registries, while Registry retrieval, last-known-good cache, and provenance stay separate from Source import and Channel materialization. Scheduling remains process-local; distributed coordination is outside this increment.
