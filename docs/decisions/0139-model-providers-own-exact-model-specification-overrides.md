# ADR-0139 — Model Providers own exact Model specification overrides

Status: Accepted — 2026-09-23

## Context

Provider discovery can omit supported model properties while still distinguishing an omitted value from an explicit rejection. Governed `Model` resources retain the provider observation, but changing that read-only observation would erase provenance and the next refresh would overwrite an administrator decision. A separate override resource would add an independently scoped lifecycle even though the decision only has meaning within one provider and one exact external model identifier.

## Decision

- `ModelSpecificationOverride` is a value object owned by the `ModelProvider`, not an independent resource.
- The provider stores a bounded `specificationOverrides` map keyed with ordinal comparison by the exact external identifier of one of its retained Models.
- Provider creation cannot contain overrides because no Models have been discovered yet. Provider updates validate that every key resolves to a retained Model owned in the same namespace and scope.
- Overrides use the Model Provider's existing authorization, revision, audit and ETag concurrency behavior. Removing a map entry removes the override.
- The retained `Model.definition.specification` remains the unmodified provider observation. Reads and profile diagnostics expose the observed specification, explicit override and computed effective specification separately.
- Effective specifications are calculated on every read and runtime resolution. Unknown observations may be completed, explicit `unsupported` support cannot be elevated, known limits can only be restricted, and provider, adapter and runtime capability intersections remain authoritative.
- Bootstrap, Packs and scheduled discovery do not create or distribute overrides in this increment.

## Consequences

An administrator can correct incomplete discovery for one exact provider model without changing another provider or another model with the same identifier. Refreshes can change the observation without losing the explicit decision, and removal restores the current observation immediately. The runtime receives the same effective model capability level shown by profile diagnostics, while provider, adapter, runtime and Tool-governance restrictions remain in force.
