# ADR-0031: Agentstration-native declarative resource envelope

Status: Partially superseded by ADR-0079 — 2026-09-07

## Decision

Management resources use `uid`, `apiVersion`, `kind`, `metadata`, and a typed `definition`. The current schema identifier is `agentstration.io/v1`. `kind` remains an extensible external string. `metadata` owns `name`, `tags`, and `annotations`.

The server generates an immutable GUID UID. ADR-0079 replaces the original ownership and logical-identity rules with explicit hierarchical scopes. References remain `{ name, workspaceRef? }`; omission selects the current workspace. Cross-workspace resolution is explicit and remains disabled until authorization semantics are delivered.

ResourceGroup, location, ARM-style paths, provider namespaces, `type/properties`, and AgentType are removed from the Management contract. Agent definitions directly contain handler, instructions, model profile, tool, behavior, middleware, context-provider, and settings declarations.

## Persistence and migration

SQLite documents keep `Uid`, `Kind`, and `Name` columns. ADR-0079 replaces the original uniqueness and migration rules with `(ScopeId, Namespace, Kind, Name)` and a full reseed before release. New writes receive a UID and updates preserve it.

Existing pre-v1 payloads are not silently reinterpreted because AgentType composition cannot be converted without policy choices. Operators must export/redeclare agents in the v1 shape or recreate a development control-plane database; startup preserves old rows rather than deleting them.

## Consequences

- JSON and YAML manifests share one canonical representation.
- URLs are short `/api/...` routes and workspace scope comes from the authorized request context.
- ETags and internal reconciliation data remain implementation concerns rather than identity.
- Future resource kinds can reuse the envelope without reproducing an ARM hierarchy.
