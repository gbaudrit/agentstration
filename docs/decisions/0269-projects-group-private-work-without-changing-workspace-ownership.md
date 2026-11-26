# ADR-0269: Projects group private work without changing Workspace ownership

## Status

Accepted

## Context

Issue #950 needs durable grouping for several WorkPlans and functional tasks before repository context or external sharing exists. A Project must not introduce another execution or authorization boundary. Workspace and authenticated Principal ownership already govern Work records under ADR-0265–ADR-0268.

## Decision

The Work module owns a Project snapshot with a typed UUID, immutable namespaced name, display metadata, explicit WorkPlan/Task associations and optimistic version. Projects are private to their Workspace and Principal. Associations reference existing identities and do not move resources, copy plan revisions, start execution or grant access. The same owned work may appear in several Projects. A Project without bindings remains useful; contextual bindings belong to #951.

Work stores retain Project data in a separate `WorkProjects` table, with Workspace/ID key, private namespaced-name uniqueness, concurrency token and bounded JSON payload. The PostgreSQL migration and SQLite initialization use the same model. A Project mutation guards referenced plan/task versions within its own store transaction. Stable command identities make creation/update replay safe without overwriting later edits. Membership is bounded to 100 plans and 100 direct tasks, and command receipts to 256 versions.

The canonical application service owns validation, privacy, lookup and mutation. REST and the Workspace-only Project Bootstrap handler reuse it. Bootstrap resolves existing private names by namespace and name and skips them to preserve local edits. An explicit portable ID must match an existing named Project; a mismatch conflicts. With no ID, Bootstrap derives a stable local identity from Workspace, Principal, namespace and name. It records the Project in the ordinary Bootstrap planning context for later references. No ProjectTemplate or generic Management business module is introduced.

## Consequences

- Workspace remains physical ownership/execution scope; Project remains logical work organization.
- Foreign or unavailable work cannot be associated. A stale Project or observed work version rejects the complete mutation.
- A concrete Workspace profile can create/reuse an ordinary editable Project through the same API/application behavior.
- External connectors, shared identities, availability resolution, active-work context snapshots and UI remain later consumers. Local rows are not a sharing protocol.
