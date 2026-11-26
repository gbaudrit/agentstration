# ADR-0290: Work task operations adopt ATC with explicit recovery guarantees

## Status

Proposed — implementation for [#955](https://github.com/gbaudrit/agentstration/issues/955).

## Context

ADR-0272 introduced optional native task operations and durable local receipts.
ADR-0289 now captures independently authorized Tracking connections. Deterministic
application actions need to consume ATC without importing vendor, MCP or SDK DTOs
into Work, and without treating every tracker target as a repository. A local durable
receipt alone cannot prove remote deduplication after a provider restart.

## Decision

Use the exact Tracking connection for typed ATC read/create/update/comment/relate/
transition operations. Infrastructure translates neutral Work inputs into the SDK
contract through the existing governed Tool pipeline and protected AEP configuration.
Current Workspace/principal authorization and captured connection evidence are checked
before dispatch; the native schema and route guards from ADR-0289 remain active.

One admitted intent maps to one ATC mutation. Retain its provider-qualified target,
original operation key, local intent and separately computed wire intent digest before
any effect. Recompute and compare that digest on resumption. Work stores neutral
capabilities and native observation evidence; SDK and transport types stop at
Infrastructure. Existing Work receipt JSON persistence provides identical SQLite and
PostgreSQL storage without a new relational model.

Expose the actual concurrency and recovery scopes, receipt window, supported fields
and relation kinds. An invalid or incomplete observation cannot succeed. ATC v1 permits
an item only on an applied receipt; a malformed partial receipt remains uncertain.
Native transition IDs and completion reasons are preserved rather than normalized to
an invented lifecycle.

Recover an uncertain mutation before considering re-execution. Validate receipt
coordinates before interpreting Missing. Only durable recovery within its declared
window may authorize replay of a missing receipt; process-local recovery can recover
an existing receipt but cannot fence a provider restart. None, expiration, cancellation,
response loss and invalid responses remain explicitly uncertain. A changed binding,
revoked grant or obsolete plan intent prevents a new effect. There are no automatic
retries or inferred exactly-once guarantees.

## Consequences

Tracking needs no repository, agent Run, LLM, forge or mandatory provider. Local plans,
internal tasks, plan activation and functional acceptance retain their own authority.
The existing native TaskOperations consumer path remains until its owning adoption
issues (#958/#1346/#1349); explicit Tracking never falls back to it. This change does
not implement GitHub/GitLab, change issue realization policy, introduce a new UX
journey or require migration of an unpublished release.
