# ADR-0050: Background Control Plane access is explicit

## Status

Accepted.

## Context

Control Plane persistence rejects calls that have neither a Workspace request context nor an explicit system context. HTTP middleware supplies a Workspace context only for the lifetime of an authenticated request. Hosted workers execute later and must not inherit or infer that context from singleton fallback state.

Two background paths need different authority. Agent deployment reconciliation is an instance operation that must inspect deployments across Workspaces. Runtime Run execution acts on behalf of the Principal who created the Run and must remain confined to that Principal's Workspace permissions.

## Decision

`IRequestContextScopeFactory` exposes distinct temporary Workspace and system scopes. `AgentDeploymentReconciliationWorker` opens a system scope around one reconciliation iteration. The scope is explicit, bounded by `using`, and does not manufacture a Principal or Workspace.

A Runtime Run persists an immutable `RuntimeRunScope` containing `TenantId`, `WorkspaceId`, and `PrincipalId`. The API derives these values from the authenticated `ICurrentRequestContext`; they are not accepted from the client. The client-provided `Initiator` field is removed, and the stored initiator is derived from the authenticated Principal.

Immediately before queued execution, Runtime reloads the Principal and Workspace and revalidates the effective `runs/execute` permission. Only after asynchronous validation succeeds does it synchronously activate the Workspace context for Control Plane resolution and agent execution. The persisted Run is authoritative; queue metadata is not an authorization source.

Runtime Runs created before this decision do not contain a durable scope and fail closed. The scope is stored in the existing serialized Runtime Run payload, so no relational migration is required.

## Consequences

- reconciliation can operate across Workspaces without weakening the default-deny Control Plane store;
- Runtime execution remains Workspace-scoped after the originating HTTP request ends or the process restarts;
- disabling a Principal or Workspace, or revoking `runs/execute`, prevents a queued Run from invoking an Agent;
- `TenantId`, `WorkspaceId`, `PrincipalId`, and initiator cannot be forged through the Runtime creation contract;
- independently queued system operations must deliberately select system authority rather than relying on an ambient default.
