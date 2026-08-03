# ADR-0015: Persisted model profiles and read-only provider APIs

## Status

Accepted — 2026-08-02

## Context

The first model-resolution increment kept profiles in application configuration. The product now needs stable profile resource IDs, CRUD, optimistic concurrency, usage visibility, deletion protection, and dynamic provider/model inspection without persisting every model discovered from Ollama.

## Decision

- `ModelProfileResource` is a canonical Management Plane resource stored by the existing SQLite control-plane document store.
- An agent continues to persist only `modelProfile.resourceId`. Agent writes require the referenced profile to exist and be structurally valid.
- Provider resources remain read-only configuration projections in this increment because `ollama-local` is managed by Aspire.
- Provider health and models are discovered dynamically through `IModelProviderDiscovery`; discovered models are not Agentstration resources.
- Runtime `IModelProfileStore` and `IModelDeploymentStore` are implemented by the Management profile service, so HTTP and Runtime resolve the same persisted definition.
- Provider or model unavailability affects computed profile status but does not invalidate or delete the profile.
- Profile deletion is rejected while an agent references its exact resource ID.
- APIs use `/api/modelproviders`, `/api/modelprofiles`, and `/api/agents/{name}/model`, with Problem Details and ETag concurrency.

## Consequences

The distinction between technical provider, dynamically discovered model, reusable persisted profile, and agent reference is explicit. Provider mutation, cached discovery, authentication, and a Management UI remain future work.
