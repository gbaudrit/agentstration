# ADR-0205: ASP uses scoped progressive content sessions

## Status

Proposed

## Context

FR #1085 extends the unreleased ASP SDK for binary, text and large content. A
single base64 payload cannot represent progressive native storage writes. Backend
conditions, publication atomicity and optional metadata differ. SDK abstraction
must not force providers to implement a shared content file or fake a guarantee.
ADR-0193 keeps generic target selection separate from authority. ADR-0200 keeps
physical configuration and AEP leases separate from content arguments.

## Decision

Replace the unreleased read/write methods directly with read_text/read_blob and
write_text/write_blob. The same method handles first and subsequent chunks. Fixed
initial options and absolute deadlines define an implicit session. Sequential
chunk identities, context-bound references and bounded replay receipts make retries
explicit. Reauthorize every call, including replay, status, abort and close.

Providers expose byte-oriented read/write handles. The SDK owns protocol state,
strict UTF-8 conversion, byte ranges, integrity checking and async Stream adapters.
Only explicit confirmed completion returns write success. Preserve the provider's
exact commit evidence; unavailable versions/digests stay absent. No later stat can
substitute for original completion metadata. Unknown outcomes never imply rollback.

Each provider advertises actual progressive, mode, condition, metadata and atomic
publication capabilities. Extension StorageTypes and backend adaptation remain
outside the provider-neutral SDK. Session cleanup, bounded replay, provider content
retention and restart recovery are independent guarantees. The SDK implements
in-process sessions and timed cleanup, without durable recovery or Snapshot retention.

## Consequences

- Initial single-call and large progressive transfers share one contract. Text and
  binary share provider I/O; text preserves exact characters and byte boundaries.
- Providers need no protocol receipt ledger or whole-object materialization.
  Memory bounds come from chunks and replay budgets rather than object buffering.
- Callers retain exact requests for bounded replay and use explicit finalization.
  Abort/disposal only releases resources; publication may already have occurred.
- This is an unreleased contract replacement with no legacy aliases or migration.
  Existing consumers change in the same PR; accepted ADRs remain unchanged.
