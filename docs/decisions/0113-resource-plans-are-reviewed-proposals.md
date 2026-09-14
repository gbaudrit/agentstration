# ADR-0113: Resource Plans are reviewed proposals

## Status

Accepted

## Context

Agentic planning must remain durable and auditable without teaching models Agentstration's canonical resource envelopes, ETags, persistence representations, or current API schemas. A plan also cannot be treated as executable desired state because it represents a proposal that may be incomplete, invalid, or rejected.

## Decision

Resource Planning is an autonomous vertical. A `ResourcePlan` is a Workspace-scoped, revisioned proposal with its own lifecycle, provenance, activity history, API, and relational storage. It is not a Management Resource kind and never participates directly in Runtime resolution.

Plan content uses an explicitly versioned functional contract. Later deterministic platform code may materialize an exact plan revision into a separate, reviewable `ResourceChangeSet`. Only that trusted layer may contain canonical Resource representations, and applying it must use the authoritative Management services.

Every read and write includes Tenant and Workspace scope. Optimistic concurrency prevents agents or users from silently overwriting concurrent refinements. Origin metadata may retain Work and FlowRun causality without transferring ownership of those records.

## Consequences

- Planning contracts may evolve independently from Management Resource schemas.
- Proposed changes have a durable review boundary before platform mutation.
- SQLite remains the local default and PostgreSQL owns a dedicated `resource_planning` schema.
- Console, Entry, Flow, MCP, and future surfaces consume the same Resource Planning application services.
- Additional storage and lifecycle concepts are accepted instead of overloading executable Resources.
