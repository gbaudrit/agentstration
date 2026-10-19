# ADR-0153: Knowledge Snapshots are immutable artifact publications

## Status

Accepted

## Context

A successful Knowledge acquisition can produce intermediate, diagnostic, staged, and durable artifacts. Treating the latest FlowRun or its complete output as current knowledge would expose non-publishable data, permit partial observations, and leave consumers without a stable provenance boundary.

## Decision

Introduce an immutable, Workspace-owned `KnowledgeSnapshot` in the Knowledge family. Publication starts from one successful acquisition manifest, selects only publishable durable `FlowRunArtifact` references, and validates the exact KnowledgeSource generation, published ingestion Flow version, successful ingestion FlowRun, Workspace ownership, producer lineage, media type, length, digest, and storage provenance before creating the complete Snapshot in one immutable resource write. Snapshot records reference artifacts and never copy their bytes or opaque backend locations.

A mutable `KnowledgeSnapshotPublication` records the bounded request, idempotency identity, pending, succeeded, or failed state, and resulting Snapshot name. Its deterministic identity rejects conflicting reuse. Retrying or recovering a pending publication reuses the same immutable Snapshot and completes any interrupted active-state update. A separate `KnowledgeSnapshotObservedState` holds the active Snapshot pointer and the last publication outcome, so activation, supersession, and failure never mutate published content. Availability is evaluated from the referenced durable artifacts when snapshots are queried.

Published Snapshot references retain their KnowledgeSource, ingestion Flow, ingestion FlowRun, storage FlowRuns, and durable artifacts. Existing deletion paths for the source and FlowRuns reject removal while a Snapshot references them; durable `FlowRunArtifact` and `KnowledgeSnapshot` resources implement the generic immutable-resource marker and have no deletion lifecycle in this increment.

## Consequences

- Consumers can select one active Snapshot or address historical Snapshots explicitly without observing a partial artifact set.
- Publication failures preserve the previous active Snapshot, and interrupted attempts can be retried or recovered safely.
- Snapshot history remains provider-neutral because physical content stays behind durable artifact receipts and Storage Flows.
- Retrieval and indexing may build on exact Snapshot identities without depending on acquisition timing.
- Snapshot garbage collection, artifact deletion policy, indexing, retrieval algorithms, and Console visualization remain separate increments.
