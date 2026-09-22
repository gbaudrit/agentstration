# ADR-0132: Foundry operator workflow reuses model resources

Status: Accepted — 2026-09-18

## Context

The isolated Foundry AEP extension and its bounded egress are available, but starting a process alone does not give a tenant administrator a Model Provider or a Model Profile. Deployment names are project-specific and may change. FR-451 supplies optional deployment and the Console journey without making Foundry part of the local default.

## Decision

- Keep the AppHost opt-in flag. Supply a Compose overlay on the existing deterministic base with an isolated AEP shared key and a local environment file ignored by Git. The overlay does not create Foundry resources and does not change the default Compose topology.
- Publish an optional tenant Bootstrap profile that creates a Model Provider bound to the configured `foundry-extension` registration and its `microsoft-foundry` contribution. It does not choose a model or create a Model Profile. Applying it requires an available extension registration and an explicit tenant selection.
- Reuse the Extensions, Model Provider and Model Profile Console pages. A discovered model links to a new Model Profile with its provider and deployment selected. The administrator still reviews and saves that resource before an Agent or Flow uses it.
- Keep the environment API key transitional; no credential enters Bootstrap resources or Model Profiles. Entra identities are supplied by the deployment environment. FR-439/FR-452 will add managed Secret Binding.

## Consequences

The standalone SQLite and deterministic startup remain offline. The Compose image carries the static Bootstrap catalog, which is inert until an administrator selects a profile. Operators can test a binding and select a real deployment through the normal product flow. Live Azure validation is opt-in and requires an existing project and credentials supplied outside the repository.
