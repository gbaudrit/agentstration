# ADR-0216: PostgreSQL JSONB uses an autonomous provider-owned layout

## Status

Proposed

## Context

[#1152](https://github.com/gbaudrit/agentstration/issues/1152) supplies the first physical implementation while preserving independent Connected Service and Storage Type contributors.

## Decision

Ship an autonomous AEP/ASP extension contributing `postgresql.jsonb@1.0.0` and a separately identifiable PostgreSQL Connected Service Type. Storage requirements bind explicit compatible typed exports; a Storage does not require the service contributor identity. Parameters and secured password values resolve through existing AEP bound values and short-lived Secret capabilities. Provider configuration carries no new credential store.

The extension owns a fixed `asp_jsonb` SQL schema with partitioned document, publication and mutation-receipt tables. Every data statement independently restricts Store UID, binding identity, collection UID and optional publication ID. Compound primary keys support isolation and identifier/keyset traversal; a JSONB path-ops GIN index supports approved containment access. General typed range/order expressions may require bounded scans; operator-managed physical tuning remains outside generic ASP.

All values, paths and cursor positions are Npgsql parameters. Only reviewed parser nodes become fixed SQL fragments. CAS revisions prevent blind overwrite/delete; mutation receipts commit atomically with the data. Publication preparation uses an immutable content/model digest, sealing controls visibility, and repeatable-read transactions keep retirement from becoming a silent empty result. Connection pools, SQL timeouts, cancellation, collection/publication counts, pages and response bytes are bounded. Schema provisioning is an explicit operator option. TLS verifies server identity except an explicit loopback-only development opt-in.

## Consequences

The default product remains SQLite and deterministic, without PostgreSQL. Physical layout is private and may change through provider migrations. Existing external databases, governed relational access, read-only SQL, full-text search, pgvector and hybrid search require future capabilities, never a breaking reinterpretation of JSONB document arguments. Performance tuning and retained data are operator responsibilities; no unbounded SQL endpoint is introduced.
