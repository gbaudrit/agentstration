# ADR-0002: Local JSON default, PostgreSQL target

Status: Accepted — 2026-07-31

The zero-dependency default persists an atomic JSON snapshot behind `IPlatformStore`. EF Core/Npgsql schema and an initial migration define the server target. This makes the first run honest and standalone while keeping PostgreSQL and pgvector as the intended multi-user store. The JSON store is not intended for concurrent multi-process use.
