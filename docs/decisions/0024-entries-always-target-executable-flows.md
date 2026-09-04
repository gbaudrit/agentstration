# ADR-0024: Entries always target executable Flows

Status: Accepted — 2026-08-06

## Context

The Console needs to let an administrator bind an Entry either to an Agent or to a Flow. Letting that choice reach Work API or Runtime would create two execution pipelines, make Entry versioning ambiguous, and leak Agent/runtime concepts into Workplace.

## Decision

An Entry draft retains the administrator's `binding` (`Agent` or `Flow`). Publication resolves that intent into one pinned `resolvedTarget` containing only a Flow resource ID and immutable published version. Workplace contracts expose `resolvedTarget`, never `binding`; Entry submission therefore always creates a WorkItem carrying an exact `FlowReference`, and the local worker always creates a FlowRun.

For an Agent binding, Agentstration persists a hidden system-managed Direct Agent Flow. Its deterministic ID is `system-direct-agent-{agentName}` and its semantic version is derived from the Agent generation (`1.0.{generation-1}`). Its immutable Direct Flow specification targets the canonical Agent resource. It is created or advanced during Entry validation/publication, marked with `systemManaged=true`, excluded from the normal Flow resource picker, and cannot be edited or deleted independently. Agent and user Flow deletion are rejected while a published Entry references them.

We selected a persisted system Flow instead of an on-demand virtual Flow because the current FlowRun service resolves immutable versions from the Flow store and records the complete published snapshot. A virtual resolver would duplicate publication, hashing, persistence, observation, and recovery behavior. Deterministic identity plus generation-derived versions prevents synchronization ambiguity while retaining the normal Flow engine.

Only `Pinned` is supported in this increment. Draft saves are isolated in the Work store and do not replace the published Entry projection. Publishing increments the Entry version and snapshots presentation, behavior, and the resolved exact Flow version.

## Consequences

- Runtime and Workplace contain no Agent-versus-Flow branch for Entries.
- Direct Agent responses and failures cross the same FlowRun normalization boundary as every other Flow.
- Updating an Agent does not silently change an already published Entry; republishing resolves the new system Flow version.
- System Flows consume normal immutable Flow storage but are hidden from user Flow selection.
- `LatestPublished`, `FollowMajor`, visual Direct Agent Flow editing, manual system Flow deletion, provider-native streaming, and attachment mapping beyond the existing Work/Flow contracts remain outside this MVP.

## Amendment — 2026-09-04

Workspace cleanup may explicitly delete an orphaned system-managed Direct Agent Flow. The ordinary Flow deletion operation still rejects system-managed resources, and the Entry deletion guard evaluates published resolved targets so cleanup cannot remove a Direct Agent Flow while any published Entry references it. This allows stale generated resources to be collected without weakening Entry execution integrity.

## Rejected alternatives

- Direct Agent execution from Work API: creates a second pipeline and leaks runtime selection into Work.
- Persisting Agent and Flow targets in the Workplace projection: forces Workplace to resolve management intent.
- A synthetic Flow resolved only at execution time: bypasses the existing immutable Flow publication and snapshot guarantees.
