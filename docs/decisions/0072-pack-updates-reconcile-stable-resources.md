# ADR-0072 — Pack updates reconcile stable resources and preserve Work history

- Status: Accepted
- Date: 2026-08-28

## Context

Development replacement originally uninstalled an existing Pack and then installed the new archive. That made replacement impossible once a Pack Entry was composed into a user-owned Dashboard or had opened a durable Interaction. Forcing deletion would erase or strand Work history, while silently rewriting Dashboard composition would violate ADR-0049.

## Decision

A Pack update is a reconciliation operation, not an uninstall followed by an install. The importer validates the complete incoming archive, checks the recorded version token for every currently managed resource, and classifies resources as additions, updates, removals, or conflicts. Resources present in both versions keep their namespace and canonical identity and are updated through their owning module in dependency order. Removed resources are processed in reverse dependency order. A local modification remains a hard conflict and is never overwritten.

An Interaction captures the published Entry used to open it. This immutable Work-owned snapshot preserves presentation, behavior, and the pinned Flow target after the active Entry is retired. Active, processing, or waiting Interactions block Entry removal. An explicit Pack removal may close idle or terminal Interactions with a durable reason; it never deletes them.

Dashboard composition remains user-owned. Stable Entry updates keep Dashboard references unchanged. Removing an Entry referenced by a Dashboard blocks by default. The operator may explicitly authorize removal from both Dashboard drafts and publications after reviewing the update or uninstall impact.

Deleting a Flow definition may remove its editable definition, draft, and published versions, but must retain durable Flow Runs and their embedded execution snapshots. Agent revision retention remains governed by its existing run-usage policy.

Pack update and uninstall persist progress in `InstalledPack`. A failure after partial reconciliation leaves the Pack degraded with the remaining managed-resource inventory and an actionable error; it never pretends that the previous installation is intact.

## Consequences

- Replacing a Pack can update an Entry already used by Workplace without deleting its identity.
- Existing Dashboard placement survives compatible updates.
- Historical conversations remain readable after uninstall and cannot accidentally start work against a deleted capability.
- Removing a live capability is explicit: active Work blocks it and Dashboard cascade requires operator intent.
- ADR-0038's development-replacement rule is superseded: replacement now reconciles in place.
- Cross-store reconciliation remains compensating rather than transactionally atomic, so durable degraded state and retry-safe version checks are required.
