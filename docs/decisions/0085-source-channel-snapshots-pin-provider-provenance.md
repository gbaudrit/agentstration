# ADR-0085 — Source Channel snapshots pin provider provenance

## Status

Accepted

## Context

A Source Channel names a mutable upstream selector through versioned provider options. Preview and application must never read that selector again after choosing content: the selected bytes, revision, and local provider generation must remain inspectable and reproducible even when the upstream ref or local binding later changes. A failed refresh must not invalidate previously usable content.

## Decision

- A manual Channel refresh requires the requested Source Version's logical binding to be ready, resolves the Channel through the selected AEP Source Provider, then materializes that exact resolved revision under bounded archive, entry, expansion, and timeout limits.
- The opaque archive is stored by its SHA-256 digest outside the control-plane document. A `SourceChannelSnapshot` is created only after AEP and artifact integrity validation succeeds.
- `SourceChannelSnapshot` is immutable and same-scope with its Source. It records the Source and Source Version identities, Channel, complete requested versioned configuration, resolved revision and integrity, artifact metadata, materialization time, and exact Source Provider UID, scope, namespace, name, generation, and contribution.
- Snapshot identity includes the resolved provider provenance and revision rather than archive bytes alone. Identical bytes from a moved revision or a changed provider generation therefore remain distinct historical snapshots.
- A mutable `SourceChannelObservedState` per Source Version and Channel points to the current usable snapshot and records the last refresh outcome. An unchanged provider provenance and resolved revision reuses the existing snapshot without materialization. Failure records a stable error while retaining the last successful snapshot pointer.
- Refreshes are serialized per Source Version and Channel in the single Agentstration process. Deterministic immutable names make retried creation idempotent. Caller cancellation does not publish failure state or a usable snapshot.
- The API exposes manual refresh, snapshot metadata queries, observed status, and a stable pin containing Snapshot UID, Source Version UID, Channel, and digest. Snapshots and artifacts are retained; garbage collection is outside this increment.

## Consequences

Downstream preview and application features can pin immutable content without coupling to Git or another acquisition transport. Provider and Channel configuration remain locale-agnostic; locale selection occurs later against a pinned snapshot. Content-addressed artifact writes may leave an unreferenced file if the following control-plane write fails, which is safe while deletion and garbage collection are deliberately absent.
