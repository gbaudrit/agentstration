# ADR-0096 — The official Source registry uses last-known-good observations

## Status

Accepted

## Context

The Registry v1 publication is external, mutable at its stable index URL, and optional to a local-first installation. Treating a remote refresh as Source import or as required startup configuration would couple already imported content to network availability. Updating cache files in place could also expose an index with only part of its compatible shards.

## Decision

- The well-known official registry is an instance-owned Management registration with desired URL and enabled state. Bootstrap creates it only when absent and never refreshes it or replaces administrator changes.
- Observed refresh state and immutable refresh records are separate resources from desired configuration. They identify the requested and final index URLs, canonical index and shard digests, HTTP validators, running Agentstration version, timestamps, outcome, and actionable error without storing catalogue content in audit events.
- Manual refresh parses the production Registry v1 index, selects every shard whose half-open Semantic Version interval contains the running version, retrieves no other shard or SourceVersion manifest, and validates each declared canonical shard digest.
- A refresh writes canonical index and shard bytes beneath a new observation directory, then atomically publishes that directory and finally advances the observed-state pointer. A failed replacement leaves the previous pointer and bytes intact and marks them stale.
- HTTP 304 records a successful check while retaining the existing observation identity and catalogue provenance. The index validators belong to that observation.
- Registry transport is bounded to public HTTPS endpoints, same-origin redirects and references, exact JSON/YAML media rules, finite time and byte limits, and connection-time address filtering. It sends no credentials.
- Periodic scheduling, retry/backoff, arbitrary registrations and credentials, merged discovery, trust evaluation, Source import, and cache retention are not part of this decision.

## Consequences

Registry outages and invalid publications are visible without invalidating imported Sources or blocking startup. Cache observations may remain unreferenced after a concurrent configuration change or failed activation; retention is deliberately deferred to the Registry orchestration increment. The registry remains metadata-only until an explicit later discovery/import workflow selects an exact SourceVersion.
