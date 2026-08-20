# Memory and execution context

Agentstration Memory is explicit, workspace-isolated persisted data retained so it may influence a future Agent execution. It is not a transcript, a generic bag of state, or an automatic copy of everything an Agent observes.

## Audit and taxonomy

| Existing mechanism | Classification | Owner | Memory? |
|---|---|---|---|
| `ConversationMessage` and `Interaction` | Functional conversation history | Work Plane | No |
| `InteractionContinuationContext` | Reconstructible projection of recent messages, results, artifact references, and continuation identifiers | Work Plane/Application | No |
| `WorkItem`, `WorkTask`, results and artifacts | Durable functional work state | Work Plane | No; selected values may be supplied as execution context |
| `FlowRun` and Runtime Run history/events | Execution record and correlation | Flow/Runtime Plane | No |
| MAF checkpoint | Opaque technical resume state | Runtime adapter | No |
| Agent definition and immutable revisions | Desired configuration | Management Plane | No; optional Memory read configuration is desired state |
| `ItemAnalysis` summary/categories | Item-owned content-analysis result | Content vertical | No |
| `MemoryRecord` | Deliberately retained fact/context with provenance and lifecycle | Memory capability | Yes |

Therefore:

**Conversation ≠ Context ≠ Memory ≠ Checkpoint.**

- Conversation is the durable functional exchange shown by Workplace.
- Context is data assembled for one execution and can be reconstructed.
- Memory is governed persisted data that may be retrieved for a later execution.
- Checkpoint is provider-adapter state used to resume the same technical execution.

## Ownership and lifecycle

A record always belongs to a server-resolved Workspace and exactly one scope:

- `Agent/{stable-agent-uid}` for one logical Agent across revisions;
- `Shared/{name}` for an explicitly named, workspace-local scope read by configured Agents.

V1 deliberately has no broad Workspace scope, Interaction scope, Work scope, or `ContextGroup` resource. Interaction, WorkItem, FlowRun, and RuntimeRun identifiers are provenance, not ownership. This avoids accidental context pollution while a named shared scope already supports several Agents sharing selected facts.

Every record answers:

- owner: Workspace plus exact Agent/shared scope;
- readers: principals with `memory/read`, further restricted by Agent configuration during execution;
- writers: principals with `memory/write` using an explicit command;
- lifetime: persistent until deletion, or bounded by `expiresAt`;
- reason/source: required provider-neutral provenance and creating Principal.

Individual delete, exact-scope clear, and bounded expiry purge are supported. Records are immutable in V1; correcting a fact means deleting it and explicitly writing a replacement.

## Read and execution assembly

```text
Work Plane
Interaction / Conversation
        │
        ▼
Context Assembly ◄──── exact-scope, bounded Memory retrieval
        │
        ▼
Runtime
        │
        ▼
Agent
        │
        └──── explicit Memory write command
```

`AgentExecutionContextAssembler` is the single Runtime-owned assembly point. It preserves conversation messages, adds explicit functional/Work context separately, retrieves configured Memory newest-first, and emits provider-neutral messages. Retrieved records are labelled untrusted contextual data rather than instructions. Workplace, Flow, the MAF adapter, and model providers do not independently rebuild history.

An Agent opts in with optional desired-state configuration:

```yaml
memory:
  readOwnMemory: true
  sharedScopes:
    - customer-support
  maximumRecords: 10
```

The configuration is optional. With no `memory` block the Agent performs no Memory read, receives no injected Memory block, and otherwise executes unchanged. The maximum is clamped to 20. V1 retrieval is exact scope plus recency; it has no query text, embedding, or LLM summarization.

## Explicit writes and API

V1 offers minimal administration and Runtime-correlation routes under `/api`:

- `POST /memory/records` writes an explicit manual record;
- `POST /runtime/runs/{runId}/memory-records` writes explicit caller-supplied content with RuntimeRun provenance;
- `GET /memory/records` lists a bounded page, optionally for one exact scope;
- `DELETE /memory/records/{id}` deletes one record;
- `DELETE /memory/records` clears one exact scope.

The client never supplies Tenant or Workspace ownership. The authenticated request context resolves both, and SQLite queries always include Workspace. Agent names supplied to the API are resolved server-side to stable Agent UIDs.

Memory is stored in the independent local SQLite database configured by `Data:MemoryPath` (default `.agentstration/memory-plane.db`). Storage is separate from Management desired state, Work conversations, Flow runs, and Runtime checkpoints.

No compatibility import from the former `memoryEntries` JSON property or `memory_entries` PostgreSQL table is performed. The generic legacy shape is replaced by the item-owned `ItemAnalysis` model. Prototype data must be reset; it is deliberately not promoted into governed Memory because it lacks the required ownership, reason, creator, retention, and provenance.

## Sensitive data and governance

No automatic capture path exists. Agent responses, conversation history, system prompts, authentication claims, secret values, credentials, tokens, Tool arguments/results, and governance traces are not copied to Memory. A Flow, Agent, or caller that derives a safe fact from a Tool result must submit new explicit content and a reason; it cannot reference non-persisted Tool arguments as an implicit source payload.

Memory content remains untrusted input at execution time. Callers are responsible for data classification before an explicit write. Logs and Runtime context-assembly events contain record identifiers/counts, not Memory content.

## Packs and future retrieval

Accumulated records are runtime/user data and are never Pack payloads. The optional Agent read configuration is portable desired state and may later participate in Pack validation/binding without exporting personal history.

`MemoryRecord`, `IMemoryRecordStore`, `IMemoryRetriever`, and context assembly are separate contracts. A semantic, tagged, explicit-reference, or hybrid retriever can replace the deterministic V1 retriever without changing record ownership or leaking a provider type into the domain. ADR-0063 adds external store providers through AEP while deliberately keeping retrieval inside Agentstration.

## Providers and profiles

The next increment makes that external boundary concrete without changing record semantics:

```text
Agent revision
    → MemoryProfile (recent retrieval, bound, retention default)
        → MemoryProvider (configured store instance)
            ├── builtin SQLite
            └── AEP extension / providerId
```

`MemoryProvider` belongs to the Management Plane. `MemoryProfile` is portable desired-state configuration. Records remain Workspace-owned runtime/user data and are addressed through an explicit provider. AEP implements only the store contract; `IMemoryRetriever` and `AgentExecutionContextAssembler` remain Agentstration responsibilities.

The AEP V1 capability supports exact-scope CRUD and expiry. It has no semantic retrieval, embeddings or provider-owned context assembly. The repository contains an offline fake provider test, not an Azure implementation.

Mutation audit is local even for external stores. It records provider, scope, operation, outcome, principal and Run/source correlation but never Memory content, tags, prompts, secrets or Tool arguments/results.

## V1 limitations

There is no vector database, embeddings, RAG/document ingestion, automatic extraction, compaction, archival, policy engine, Workplace transcript projection, dedicated Flow steps, Workspace-wide scope, or built-in distributed/cloud store. The Console surface is limited to administrative inspection, provider testing and explicit deletion; it is not a user-facing “what the Agent remembers” experience. Multi-agent MAF orchestration-specific context injection is deferred; Runtime Run and the current simple Work/Flow execution path use the common assembler.
