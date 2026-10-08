# ADR-0151: Artifacts use ToolSet-backed staging and Storage Flows

## Status

Accepted

## Context

Any Flow or Agent may produce intermediate content that must cross a process or model boundary before it becomes durable output. Agentstration cannot assume that temporary or durable bytes live on its filesystem: an operator may supply an MCP, REST, or AEP-backed Tool. Passing local paths or provider credentials to a remote model would break isolation, while binding persistence directly to the producer would prevent reusable storage policies.

## Decision

Introduce an Artifacts resource family. A Workspace-owned `StagedArtifact` records provenance, integrity metadata, retention, leases, and an opaque backend resolution. Its physical bytes are handled by one exact published ToolSet version selected through an `ArtifactStagingBinding`. The ToolSet must resolve the five unit capabilities `create`, `write`, `read`, `stat`, and `delete`; each selected Tool still passes through the governed Tool execution pipeline.

Agentstration ships a local filesystem ToolSet and default binding. The filesystem is an adapter, not part of the artifact contract. Other bindings may select Tools supplied through any supported Tool provider, including MCP and AEP.

Handoffs explicitly choose reuse, copy, move, or reuse-or-copy and issue bounded consumer leases. Remote consumers obtain content through the public staged-artifact broker Tools; backend references, paths, and credentials are never disclosed. Low-level staging Tools are projected for governed routing but are hidden from the internal MCP surface and reject direct Agent or Runtime invocation.

Durability is Flow composition. A producer calls a Storage Flow implementing `artifact.storage.write/v1`; its normalized receipt completes an immutable `FlowRunArtifact`. Retrieval calls a Flow implementing `artifact.storage.read/v1` and materializes a new staged artifact. The built-in write and read Flows use a separate local durable filesystem adapter, but operators may replace the Flow with arbitrary governed Tool and Agent steps.

## Consequences

- Artifacts may be emitted at any Flow step and are not restricted to a content-specific name or terminal output.
- Temporary transfer and durable persistence are independent choices, each scoped to the Workspace.
- Tool remains the unit of authorization, approval, and audit; ToolSets provide deterministic backend capability routing.
- Leases, retention, chunk limits, digests, idempotency keys, and cleanup retries are enforced before provider-specific behavior.
- Work projects durable artifact references instead of copying artifact bytes into the Work store.
- The local executable remains self-contained while MCP, REST, and AEP storage implementations remain possible without changing the Artifacts domain.
