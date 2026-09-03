# ADR-0078: PostgreSQL is an optional server storage profile

Status: Accepted — 2026-09-03

## Decision

Agentstration supports `Sqlite` as the zero-dependency default and `PostgreSql` as an optional server storage profile. PostgreSQL uses one database with module-owned `management`, `work`, `flow`, `runtime`, `identity`, and `scheduler` schemas. Each EF Core module owns its migrations and history table. Quartz remains a non-clustered reconstructible projection; the product remains one modular-monolith process and PostgreSQL does not imply multi-instance support.

File-backed secrets, Data Protection keys, Pack archives, and Work artifacts remain file-backed. Switching providers never imports, alters, or deletes SQLite files. The first PostgreSQL increment targets an empty database; a future, separately validated export/import tool may migrate data.

Startup validates the provider and connection, obtains a bounded PostgreSQL advisory lock, creates the schemas, applies migrations in deterministic order, initializes Quartz, and only then allows bootstrap and workers to proceed. Readiness stays unhealthy until this completes.

## Consequences

Operators must back up the PostgreSQL database and the file-backed state together. Restores must keep the database, Data Protection key ring, secrets, Packs, and artifacts consistent. Default tests remain offline against SQLite; PostgreSQL contract and smoke tests are opt-in through `AGENTSTRATION_TEST_POSTGRES`. PostgreSQL improves write-concurrency headroom but no performance claim is made without a measured benchmark.
