# Persistence

Standalone mode deliberately separates module-owned persistence. The default SQLite profile uses distinct local database files:

| Responsibility | Current local store |
| --- | --- |
| Resource families and desired state | `control-plane.db` |
| Resource planning | `resource-planning.db` |
| Identity, authorization, preferences, and security audit | `identity.db` |
| Trigger scheduling projection | `scheduler.db` |
| Work Plane and Workplace projections | `work-plane.db` |
| Flow definitions, versions, drafts, Runs, and events | `flow-plane.db` |
| Runtime Runs, attempts, and events | `runtime-plane.db` |

The same data directory also contains bounded file-backed state where relational storage is not the owning abstraction: encrypted local secret material under `secrets/`, ASP.NET Core data-protection keys, Pack archives, Source snapshots and caches, and Work artifacts. These are not a legacy JSON content store.

PostgreSQL is an optional server storage profile. It may consolidate relational infrastructure physically, but module ownership remains separated by the `management`, `work`, `flow`, `runtime`, `identity`, and `scheduler` schemas. Resource planning remains an explicit module boundary in either profile.

Every owned query carries its canonical instance, Tenant, or Workspace scope. Published Flow versions and Agent revisions are immutable. Raw ingested Source content is preserved and is never overwritten by normalized or generated output.

These stores are implementation details behind provider-neutral ports; no external database is required by default. See [ADR-0007](../../decisions/0007-sqlite-control-plane.md) and [ADR-0078](../../decisions/0078-postgresql-is-an-optional-server-storage-profile.md).
