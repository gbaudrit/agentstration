# ADR-0270: Project context is authorized and captured at task admission

## Status

Accepted

## Context

Issue #951 adds typed contextual resource references to the private Projects delivered by #950. Project settings may change while WorkPlan attempts execute. A new binding must not silently retarget already admitted work, and the Work module must not depend on Git DTOs, provider configuration or Secret values.

## Decision

Project definitions retain bounded named bindings with Context, Sharing or Repository purpose. Each binding identifies an exact scoped resource address, UID, generation and optional resource version. The first supported targets are ToolProvider, ConnectedService and immutable SourceVersion identities. Sharing selects one ToolProvider; Repository selects one ToolProvider or ConnectedService. These are resource references, not a transport-specific connection implementation. #1345 owns the sharing connection and #955/#958 own external task/repository operations.

The Infrastructure adapter validates the caller's exact Workspace/Principal and read permission before entering the declared ownership tree. It reads identity metadata through the existing resource identity catalog and checks the existing Discover and Use policies independently. Missing, foreign, replaced, stale and denied references produce bounded invariant diagnostics. It never resolves configuration bodies, endpoint credentials, Secret values or remote content. Availability means a current authorized reference; remote health and operation-specific capabilities remain checks of the actual consumer.

WorkPlan execution explicitly selects a private associated Project by ID. Admission captures the current Project version and resolved resource identities, ETags and policy decision reasons. The capture and Project version guard accompany the existing transactional task claim and dispatch intent. Work mutations acquire task rows, then plan rows, then Project rows, avoiding opposite lock order between grouping updates and execution admission. A concurrent Project edit rejects admission without creating an execution.

Each attempt retains its own immutable context snapshot and digest. Root Work metadata includes Project ID, selected version and context digest, correlated with the existing plan/attempt/Flow identities. Editing a Project uses optimistic versioning and affects future admissions only. Submission recovery never captures a newer context for an admitted attempt. Explicit technical retry creates another attempt and captures current context. Revocation does not erase earlier evidence; consumers must still authorize the captured references when using them and cannot fall back to a newer reference silently.

These additions extend Work JSON snapshots without relational changes. Existing Projects default to no bindings, existing executions default to no Project and existing attempts to no context. Digest construction preserves replay of pre-binding Project and standalone execution commands. No automatic template propagation, remote synchronization or resource execution is introduced by binding authoring.

## Consequences

- Generic local plans remain independent of Projects and providers.
- Current binding diagnostics and admitted context evidence are distinct API views.
- Active work preserves its selected context even after a Project edit, resource change, access revocation or server restart.
- The guard is atomic for Work-owned Project state. Management identity/access observations use their existing boundaries; actual resource consumers reauthorize before effects.
- Work persists references and local evidence only. Shared formats must map portable identities instead of copying local Workspace IDs or these database snapshots.
