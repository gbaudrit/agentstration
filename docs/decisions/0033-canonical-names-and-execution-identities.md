# ADR-0033: Canonical resource names and explicit execution identities

Status: Accepted — 2026-08-12

## Context

ADR-0031 replaced hierarchical Management identifiers with the Agentstration-native resource envelope, but Flow, Workplace, Runtime contracts, HTTP clients, and Console routes still carried pieces of the former path. The execution model also exposes several identifiers whose distinct ownership was not documented clearly.

## Decision

Every Management, Flow, and Workplace resource is addressed at its public module boundary by its canonical name. Tenant and workspace are authorization and query scopes, never path fragments embedded in a resource identifier. HTTP routes use the resource kind and name, for example `/api/agents/sql-expert` and `/api/flows/prepare-report`.

Existing Management scope remains `(Tenant, Workspace, Kind, Name)`. Work-owned queries require `WorkspaceId`. Cross-workspace references remain disabled unless a future authorization decision explicitly enables them.

Execution identities remain separate because they describe different lifecycles:

- `InteractionId` identifies a durable Work conversation.
- `WorkTaskId` identifies the functional anchor task and uses the anchor `WorkItem` identity.
- `FlowRunId` identifies one Flow-owned graph traversal.
- Runtime Run ID identifies one Runtime-owned exact agent invocation.

Correlation between these records does not transfer state ownership. Direct Runtime execution does not create a Work interaction or task. Retrying a technical execution creates a new Run and retains correlation to the functional identity when applicable.

## Persistence and migration

JSON deserialization tolerates obsolete payload properties so existing documents remain readable. Runtime SQLite startup detects the obsolete mandatory scope column, rebuilds the Run table without it, preserves every Run row, and leaves the independent event table intact. New schema compatibility is covered by an offline migration test.

## Consequences

- Public contracts and Console URLs no longer expose a discarded hierarchy.
- Workspace isolation is explicit in storage queries instead of encoded in identifiers.
- Operational tooling can distinguish functional retry, Flow replay, and agent-invocation retry.
- Historical ADR text remains available, but ADR-0025 is marked partially superseded for its former resource hierarchy.
