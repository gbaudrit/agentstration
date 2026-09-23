# ADR-0135 — Work conversations and tasks are principal-owned

## Status

Accepted

## Context

Workspace isolation prevents access across Workspaces, but it does not isolate two authenticated principals that share one Workspace. Conversations and functional Work tasks contain user-specific history, inputs, results, and pending actions. Treating the Workspace as their only ownership boundary therefore exposes one principal's Work to another principal in the same Workspace.

Management resource scope, Entry exposure, and Work ownership solve different problems. Resource scope identifies where a definition is governed, exposure identifies where an Entry is presented, and Work ownership identifies which authenticated principal may observe or mutate an execution record.

## Decision

Every `WorkplaceInteraction` and `WorkItem` stores one immutable `OwnerPrincipalId`. The Application derives it exclusively from the trusted authenticated request or execution scope; public commands and transport contracts do not accept an owner. A WorkItem created from a conversation must have the same owner as that conversation. Public Tasks are projections of their anchor WorkItem and inherit its ownership rather than storing a separate owner.

User-facing repositories and application services require `(WorkspaceId, OwnerPrincipalId)` for list, lookup, continuation, action, and deletion operations. They return only the current principal's records and expose no owner query filter. Internal projection and execution consumers that must resolve Work independently of a live user request use explicitly named system lookup methods and do not change the stored owner.

An Entry administration operation may inspect whether retained conversations exist, but it cannot close or otherwise mutate a conversation owned by another principal. Entry deletion is refused in that case until a future explicit ownership-override policy exists.

SQLite and PostgreSQL persist the owner as a required column and index owner-scoped list paths. Because the product remains pre-release, the current baseline schemas are updated directly; existing local databases must be recreated and no backfill or compatibility migration is provided.

## Consequences

Two principals in the same Workspace cannot list, retrieve, continue, answer, or delete each other's conversations or Tasks through supported user paths. Work created without a conversation is still owned by the authenticated principal that submitted it. Correlation, retries, Entry exposure, Management visibility, and Workspace membership do not transfer ownership.

Sharing, administrator override, ownership transfer, team-visible conversations, and visibility policies remain outside this decision. They require explicit future contracts and authorization semantics rather than weakening the owner-scoped default.
