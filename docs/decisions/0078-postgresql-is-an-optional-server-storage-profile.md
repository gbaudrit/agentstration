# ADR-0078: PostgreSQL is an optional server storage profile

Status: Accepted — 2026-09-03

## Decision

Agentstration supports `Sqlite` as the zero-dependency default and `PostgreSql` as an optional server storage profile. PostgreSQL uses one database with module-owned `management`, `work`, `flow`, `runtime`, `identity`, and `scheduler` schemas. Each EF Core module owns its migrations and history table. Quartz remains a non-clustered reconstructible projection; the product remains one modular-monolith process and PostgreSQL does not imply multi-instance support.

This decision supersedes only the SQLite-specific Quartz JobStore placement in ADR-0068 when the PostgreSQL profile is selected. ADR-0068's source-of-truth, reconciliation, authorization, and idempotency decisions remain unchanged.

File-backed secrets, Data Protection keys, Pack archives, and Work artifacts remain file-backed. Switching providers never imports, alters, or deletes SQLite files. The first PostgreSQL increment targets an empty database; a future, separately validated export/import tool may migrate data.

Startup validates the provider and connection, obtains a bounded PostgreSQL advisory lock, creates the schemas, applies migrations in deterministic order, initializes Quartz, and only then allows bootstrap and workers to proceed. Readiness stays unhealthy until this completes.

Aspire uses a slot-scoped native Docker volume rather than a worktree bind mount because PostgreSQL initialization requires Unix ownership and permission changes. The generated database password is persisted through the AppHost user-secrets store. A slot volume and its persisted password are one operational credential set.

## Consequences

Operators must back up the PostgreSQL database and the file-backed state together. Restores must keep the database, Data Protection key ring, secrets, Packs, and artifacts consistent. Removing a slot volume is a destructive reset and never occurs automatically. Default tests remain offline against SQLite; PostgreSQL contract and smoke tests are opt-in through `AGENTSTRATION_TEST_POSTGRES`. PostgreSQL improves write-concurrency headroom but no performance claim is made without a measured benchmark.
