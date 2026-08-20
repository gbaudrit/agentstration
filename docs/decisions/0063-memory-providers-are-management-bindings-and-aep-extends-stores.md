# ADR-0063: Memory providers are Management bindings and AEP extends stores

## Status

Accepted.

## Context

ADR-0062 established governed Memory records and Runtime-owned context assembly with one local SQLite store. Multiple storage technologies must be selectable without allowing a backend, AEP, or a retrieval strategy to own Agentstration's Memory model.

## Decision

`MemoryProviderResource` is a desired-state Management resource describing one configured store integration. V1 supports the builtin SQLite integration and an AEP integration identified by `extensionId` plus the extension's `providerId`. `MemoryProfileResource` references a provider and contains reusable recent-retrieval limits and optional default retention. An Agent optionally references a profile and retains only its own/shared scope selection.

All record administration is explicitly provider-scoped. Runtime resolves Agent revision → profile → provider on the server. There is no implicit Workspace provider, routing index, `MemoryStore` resource, or `ContextGroup`. A shared scope is shared only by Agents that use the same Workspace, provider, and scope name.

AEP exposes `aep.memory-provider` as a store capability: bounded CRUD, exact-scope listing, expiry, clear-scope and Workspace-scoped purge. Retrieval policy and context assembly remain inside Agentstration. AEP DTOs do not reference Memory, Runtime, MAF, Azure, or provider SDK types.

Memory mutations are audited locally before and after provider invocation. Audit records contain identifiers, scope, provenance correlation, outcome and counts, never content, tags, prompts, credentials or Tool payloads.

## Consequences

- SQLite remains the offline default and AEP is optional.
- An Azure implementation can be delivered as an extension without changing `MemoryRecord` or Runtime contracts.
- Provider bindings cannot be edited to silently point existing records elsewhere.
- Profiles are Pack-portable; providers are installation bindings; Memory records are never exported.
- V1 AEP does not expose semantic or hybrid retrieval and no Azure SDK is included.
