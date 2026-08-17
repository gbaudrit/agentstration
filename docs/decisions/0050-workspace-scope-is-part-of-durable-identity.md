# ADR-0050: Workspace scope is part of durable identity

## Status

Accepted

## Context

Management resources were already filtered through the current tenant and workspace context, but Runtime runs, Flow resources and runs, Work items, execution events, queues, cancellation state, and artifacts did not all carry the same first-class scope. Entity identifiers could therefore be treated as globally unique by some storage and background-processing paths.

## Decision

Every workspace-owned durable resource includes a required `WorkspaceId`. Its storage identity and lookup key include that workspace alongside its local identifier. This applies to Management resources, Runtime runs and events, Flow definitions, drafts, versions, runs and events, Work items and execution events, queue envelopes, cancellation registries, SignalR subscriptions, and filesystem artifacts.

HTTP callers do not select this scope through payloads or query parameters. Endpoints derive it from the authenticated request context. Background workers carry the workspace in their queue envelope, compare it with durable state, and re-authorize the workspace before execution.

The schema change is intentionally reset-only. Agentstration does not alter or backfill older SQLite databases for this increment; development and standalone installations must delete their generated databases and reseed.

## Consequences

- The same local resource or run identifier can safely exist in different workspaces.
- Model Providers and Runtime Profiles are scoped through the same Management store rules as every other Management resource.
- Cross-workspace reads, mutations, event observation, cancellation, and artifact access fail as missing or unauthorized operations.
- Existing generated SQLite databases are incompatible and must be recreated. No migration or compatibility shim is maintained.
