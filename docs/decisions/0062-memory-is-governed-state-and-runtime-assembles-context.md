# ADR-0062: Memory is governed state and Runtime assembles execution context

## Status

Accepted.

## Context

Agentstration already persisted several kinds of state that can be mistaken for memory: `ConversationMessage`, `InteractionContinuationContext`, Work results and artifacts, Flow and Runtime Runs, and opaque Microsoft Agent Framework checkpoints. The original content MVP also called generated summaries `MemoryEntry` and exposed keyword search over them. That name described an experiment, not durable information deliberately retained to influence a future Agent execution.

Conflating these mechanisms would make retention, ownership, authorization, replay, and provider boundaries ambiguous. In particular, a MAF checkpoint is technical resume state, and an Agent resource/revision is desired state that a Run must never silently mutate.

## Decision

Memory is a dedicated Agentstration capability with provider-neutral domain, application, storage-abstraction, and local SQLite projects. A `MemoryRecord` is workspace-owned persisted data that may influence a future execution. It has one exact scope, content, tags, provenance, creator, creation time, and optional expiry.

V1 supports two scope kinds:

- `Agent`, keyed by the stable Agent UID;
- `Shared`, keyed by an explicit workspace-local name.

There is no implicit Workspace-wide scope and no `ContextGroup` resource. A named shared scope meets the multi-Agent sharing requirement without adding desired state or a separate lifecycle. Interaction and Work remain sources of provenance or execution context; they do not become Memory owners in V1.

Reading and writing are separate decisions. Agent configuration may opt into bounded reads of its own scope and named shared scopes. Writes are only explicit API/application commands. Agent replies, prompts, Tool arguments/results, traces, conversations, and checkpoints are never captured automatically.

Runtime owns `AgentExecutionContextAssembler`. It combines ordered provider-neutral conversation messages, explicit functional/Work context, and bounded Memory retrieval into distinct message blocks immediately before execution. Work and Flow supply inputs and projections but do not implement retrieval or storage. The MAF adapter only translates the already assembled messages and never owns Memory contracts.

Memory data is runtime/user state and is never exported by Packs. Agent Memory read configuration is desired state and may later be portable in a Pack. AEP is unchanged.

The old generic content `MemoryEntry` is removed. Its useful content-workflow result becomes the narrower item-owned `ItemAnalysis` model; nullable Mission ownership, generic kind/content fields, keyword search, JSON compatibility, and the old PostgreSQL table are removed. Prototype-generated local data is reset rather than migrated into governed Memory because its semantics and provenance are insufficient.

## Consequences

- Every query and mutation requires the canonical server-resolved `WorkspaceId`; record identity is composite with workspace ownership.
- Dedicated `memory/read`, `memory/write`, and `memory/delete` permissions govern the REST and Runtime paths.
- Retrieval is exact-scope, newest-first, deterministic, and bounded to 20 records in V1.
- Expired records are excluded from reads and can be purged; individual delete and clear-scope are supported.
- A future semantic or hybrid retriever can implement `IMemoryRetriever` without changing `MemoryRecord` or Runtime execution contracts.
- V1 has no embeddings, automatic extraction, Workspace-wide memory, UI studio, Flow designer steps, distributed store, or multi-agent orchestration-specific injection.
