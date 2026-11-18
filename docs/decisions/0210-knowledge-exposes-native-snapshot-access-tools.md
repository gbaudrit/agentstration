# ADR-0210: Knowledge exposes native Snapshot access Tools

## Status

Proposed

## Context

An Agent may consult a Knowledge Source before acquisition or projection exists.
It also needs bounded progressive reading, exact passages from regex search, and
an independent way to compare its pinned Snapshot with active and latest published
content. Routing these operations through a mandatory retrieval Flow adds no
transformation and conflates content access with preparation. Feature #1119 owns
this change; #1115 and #1117 remain later, non-blocking lifecycle and composition
extensions.

## Decision

Expose `read`, `search_regex`, and `get_status` as source-specific immutable relay
ToolDefinitions targeting platform Knowledge handlers. Literal bindings fix the
exact source owner, namespace, name and UID; callers cannot replace that binding.
The existing Tool pipeline owns assignments, approvals, hooks and execution audit.
Tools and their `.access` ToolSet follow the selected logical Instance, Tenant or
Workspace scope (ADR-0209). Receipts, preparation, Snapshots and read leases remain
isolated in the invoking operational Workspace.

`read` and `search_regex` can each be the first operation. Without a Snapshot or
cursor they reuse the usable active Snapshot or invoke the existing governed
projection/acquisition services, respecting each binding's current freshness and
refresh policy. Automatic preparation uses parameters saved with the Source.
Compatible overlapping requests share preparation within the same caller,
Workspace, source configuration and active Snapshot. A pending result returns an
opaque preparation ID and retry guidance. Retrying that ID resumes the same
idempotent attempt; a new explicit call after failure may start a new attempt.
Caller cancellation does not cancel an accepted preparation; host shutdown stops
it and existing projection recovery owns recorded attempts.

`read` opens and continues content; no separate open Tool is required. New calls
may explicitly select a Snapshot and document, optionally a character range.
Ranges use Unicode scalar offsets, inclusive start and exclusive end. Pages
respect a character budget and return the exact Snapshot and document identities.
`search_regex` uses bounded .NET non-backtracking expressions with a timeout. It
returns exact match ranges, surrounding read ranges and excerpts. Read ranges are
accepted directly by `read`. Regex and reading can precede one another; neither
requires a cursor produced by the other. Patterns that split a Unicode scalar
are rejected rather than returning broken text.

Opaque protected cursors bind the operation, source, logical consumer scope,
principal, Tenant, Workspace, exact Snapshot UID, position and expiry. Each call
reauthorizes source and content access. Continuations never silently switch to a
new active Snapshot. Durable read leases last one hour and use 256 conditional
slots per Workspace; compatible calls reuse a lease without extending its expiry.
Future retention work in #1115 must honor unexpired leases before removing content.
Current retained publications are sealed and are not garbage-collected.

`get_status` reports active and latest published Snapshots independently, compares
an optional Snapshot, exposes recorded input freshness, processing/failure evidence
and document media/size information. It performs no acquisition, projection or
remote-origin freshness probe. A fresh recorded acquisition is not a promise that
its origin has not changed.

Native access bypasses retrieval Flows and does not invent Flow Runs. Preparation
and transformation still use real governed Flows. Custom historical retrieval
Flows and their REST endpoints remain optional. This supersedes the mandatory
retrieval invocation for native access in ADR-0154 and the generated Flow Tool
exposure shape in ADR-0183; accepted historical ADRs remain unchanged. Existing
Snapshot and Artifact provenance stays intact. New `.access` publication names
avoid mutating earlier immutable Tool versions; no compatibility aliases are added.

## Consequences

- No vector search Tool is introduced. A later `search_vector` can coexist with
  `search_regex` without changing its matching semantics.
- The bounded initial text profile accepts at most 1 MiB per document, 8 MiB per
  regex scan, 512 pattern characters, 100 matches and 32,768 output characters.
  Binary formats require an explicit governed preparation transformation.
- Retained output still requires the qualified builtin local publication adapter.
  This feature does not add Azure/OpenDAL retained-publication support.
- #1115 does not gain TTL/direct-handoff/history behavior through this change;
  #1117 can later extend the existing preparation service without changing these
  Agent-facing content contracts.
- Console consultation uses the same service as HTTP and Tools. Deferred reusable
  UX coverage is tracked in #1120, after maintainer validation and PR merge.
