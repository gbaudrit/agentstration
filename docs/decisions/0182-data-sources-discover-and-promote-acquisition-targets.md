# ADR-0182: Data Sources discover and promote acquisition targets

## Status

Accepted

## Context

ADR-0164 makes a Data Source the authority for governed acquisition and ADR-0167 limits Knowledge Sources to projecting acquired artifacts. Some origins expose several independently selectable collections, partitions, or operations whose identities cannot be known when the Data Source Profile is authored. Encoding those provider-specific identities in the platform contracts would leak transport details and would make a rediscovery silently change what an existing Knowledge Source consumes.

Flow schema binding resolution is being standardized separately by FR #759. Discovery must not create another schema resolver or couple Data Source contracts to the current inline Flow schema representation.

## Decision

A Data Source Profile may select a published Flow with `flow.contract: datasource.discovery/v1` and a separate bounded set of discovery Tool bindings. A discovery is a Workspace-owned durable execution record with the same terminal and waiting states as acquisition. It pins the Data Source generation, resolved Profile revision, Flow, Tools, limits, policies, caller, correlation, idempotency evidence, artifacts, and a bounded hierarchy of provider-neutral choices. Choice selectors are opaque bounded JSON owned by the profile implementation; platform contracts contain no transport-specific fields.

A discovered choice becomes usable only through an explicit promotion to a named Workspace-owned `DataSourceAcquisitionTarget`. Promotion snapshots its selector, discovery evidence, Data Source generation, and Profile revision. A later successful discovery, Data Source change, or Profile revision change makes the target stale. Rediscovery never updates or selects a target automatically.

Profiles without discovery preserve targetless acquisition compatibility. Profiles with discovery require one enabled, current target for each acquisition. Concurrency is evaluated per Data Source and target. Acquisition records snapshot the selected target, and Knowledge Data Source bindings may reference that target. Knowledge resolves only successful acquisitions for the exact target and never invokes discovery or the external origin.

Until FR #759 lands, the adapter validates the effective schemas exposed by the existing published Flow resolver. This is an isolated compatibility seam, not a new resolution mechanism. The final integration must consume only the canonical resolved and snapshotted SchemaBindings from FR #759.

## Consequences

- Discovery remains provider-neutral and can be supplied by extensions through ordinary governed Flows and Tools.
- Administrators explicitly control which discovered choices become acquisition and Knowledge inputs.
- Existing acquisitions remain reproducible when the origin catalog changes.
- SQLite and PostgreSQL use the existing generic Resource store for discoveries and targets; no provider-specific relational model or migration is required.
- The temporary Flow schema validation seam must be reconciled after FR #759 merges before the feature pull request is finalized.
