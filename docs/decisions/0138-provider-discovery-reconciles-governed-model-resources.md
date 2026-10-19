# ADR-0138 — Provider discovery reconciles governed Model resources

Status: Accepted — 2026-09-22

## Context

ADR-0015 kept provider model discovery as a dynamic read because the early provider contract exposed only transient names and loose capabilities. Typed, bounded AEP observations now provide enough provider-neutral state to retain a useful Model inventory. Repeating remote discovery during GET requests makes the Console and profile diagnostics depend on provider availability, loses disappeared models, and offers no stable resource identity for governance or overrides.

## Decision

- A provider-discovered `Model` is a Models-family Control Plane resource. It inherits the owning Model Provider namespace and ownership scope.
- Discovery identity is the immutable Model Provider UID plus the exact external model identifier. The resource name is a deterministic, collision-resistant projection of that identity and is not the provider model identifier.
- Only an authorized explicit refresh command calls provider discovery. Model list, get, provider inventory and profile-resolution reads use retained resources and never trigger discovery or writes.
- Refresh is bounded to 1000 observations and reconciles created, updated, unchanged, missing and reappeared Models. Missing Models are retained rather than deleted.
- A failed refresh records a failed observation state while preserving the last valid identity and typed specification. Cancellation requested by the caller is propagated without converting it into a discovery failure.
- Direct administrative Model create, update and delete endpoints do not exist. Deleting an otherwise-unused Model Provider removes its provider-owned Model inventory in the same authorized scope operation.
- Model Profiles continue to select the owning provider plus exact external model identifier. Effective specifications are calculated later and are not stored on the Model resource.

## Consequences

Administrators can inspect stable Models even while a provider is unavailable or after a model disappears, and providers exposing the same external identifier cannot collide. The generic Control Plane document stores require no relational migration. Bootstrap, Packs and scheduled refresh remain out of scope. This decision supersedes ADR-0015 only for the persistence of bounded provider-owned Model observations; provider health remains an observed view.
