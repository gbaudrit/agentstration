# ADR-0041: Identity schema and Web key material are durable

Status: Accepted — 2026-08-16

## Context

The first local-account vertical created `identity.db` with EF Core `EnsureCreated`. That mechanism cannot apply incremental schema changes and does not maintain migration history. Existing local installations may already contain accounts created with that schema and must not lose them when migrations are introduced.

ASP.NET Core authentication cookies and Identity lifecycle tokens are protected by Data Protection. An ephemeral or instance-specific key ring invalidates every session and outstanding token after restart and cannot support multiple replicas of the same Web application.

## Decision

`Agentstration.Security.AspNetCoreIdentity` owns versioned EF Core migrations for its dedicated SQLite database. Startup runs `MigrateAsync` before bootstrap or authentication traffic is accepted.

For the one-time transition, startup detects an existing pre-migration Identity database. It verifies that every table from the original Identity schema is present, creates `__EFMigrationsHistory`, and records the initial migration without recreating or modifying account data. A partial legacy schema fails startup with an explicit error instead of being treated as migrated. New and already migrated databases follow the normal EF Core migration path.

The Web host persists Data Protection keys in a dedicated directory. The default is `data-protection-keys` beside the local data file; `Agentstration:Authentication:DataProtectionKeysPath` overrides it. Startup resolves the path, creates the directory, and verifies that it is writable. The Data Protection application name is the stable value `Agentstration`.

The key directory is sensitive operational state. Agentstration does not invent key encryption. Operators must protect it with operating-system permissions, backup it with the Identity database, and use a standard Data Protection key-encryption mechanism when their deployment requires encryption at rest.

## Consequences

- Future local-account schema changes are additive, reviewable migrations.
- Existing accounts survive the transition from `EnsureCreated`.
- Cookies and standard Identity tokens remain valid across normal process restarts while the key ring is preserved.
- Multiple replicas can share sessions only when they deliberately share the same key ring and application name.
- Restoring `identity.db` without its matching key ring preserves accounts but invalidates existing cookies and lifecycle tokens.

## Follow-up

Document deployment-specific key encryption and shared key-ring profiles when non-standalone hosting is introduced. Account lifecycle features must add schema changes through migrations and must include upgrade tests from the previous migration.
