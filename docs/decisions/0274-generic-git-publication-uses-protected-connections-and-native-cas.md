# ADR-0274: Generic Git publication uses protected connections and native CAS

## Status

Proposed — implementation for [#1344](https://github.com/gbaudrit/agentstration/issues/1344).

## Context

The existing Git executable supplies public Source acquisition under ADR-0081/0083.
Project sharing and Coding also need authenticated reads and recoverable writes. These
operations must not interpret WorkPlans, Bootstrap documents, usage records or issues.
Published Tool relays already provide per-consumer fixed configuration and execution-only
Secret resolution (ADR-0202); introducing another Git executable would duplicate them.

## Decision

- Extend `Agentstration.Extensions.Git` with a distinct generic AEP/MCP operations
  contribution, `git.repository`. Preserve the existing Source contribution and its
  immutable-commit archive contract, including its public-only credentials policy.
  Operations are disabled by default and require an explicit repository allowlist.
- Publish capability metadata `agentstration.git.repository`, version `1.0`, for
  resolve, exact read, bounded binary diff and publication with expected-base/recovery
  support. GitHub/GitLab issue and PR semantics remain outside this extension.
- Use existing protected Tool relays to bind repository, authentication scheme,
  username, credential and allowed ref/path prefixes. Resolve Secrets only at execution.
  Use Basic/Bearer authentication through process-scoped, exact-URL Git configuration
  environment entries. Never put a credential in argv, URLs, journals or diagnostics.
  None authentication is explicit; ambient credential helpers are disabled.
- Allow HTTPS, with redirects disabled for operations. Local-file and HTTP-loopback
  transports require explicit fixture options. Reuse a concrete bounded Git command
  runner with the Source path, preserving Source redirect defaults. Remove inherited
  Git configuration environment, system/global configuration, hooks, submodule recursion,
  signing and unapproved protocols. Run Git without a shell, checkout, filters or user
  scripts; discard stderr and return bounded technical codes.
- Bound active operations to four, waiting admission to 32 callers/one second, operation
  time, repository bytes, stdout, file count and aggregate changes/content. Require exact
  lowercase 40-character commit SHAs in this protocol version. Read only regular blobs
  at permitted descendant paths and commits reachable from the explicitly allowed ref.
  Return diff bytes as base64 to preserve binary/non-UTF-8 data.
- Build changesets through Git objects and a private index. Preserve unrelated tree
  entries, reject traversal and `.git` path components, preserve executable mode unless
  explicitly changed, and never follow a repository symlink. Use a synthetic author,
  a journal-pinned timestamp and operation/intent trailers to produce a stable candidate.
- Persist a provider-owned journal under an opaque operation key with an exclusive file
  lease, flushed temporary file and atomic replacement. Prepared state proves that push
  has not started. Persist candidate and Sent before invoking push. Scope the immutable
  intent by exact repository authority, input and caller-provided intent digest; exclude
  credentials. Invalid phases or inconsistent receipts fail closed.
- Enforce the native expected-ref compare-and-swap with an explicit Git ref lease.
  Existing refs require `BaseRevision == ExpectedRefRevision`; the only constructed
  candidate is its direct child. A new ref requires an absent-ref lease and may have
  an authorized base or be a root commit. `--force-with-lease=<ref>:<expected>` is used
  solely for this old-value check; there is no `--force` path, arbitrary candidate,
  history rewrite, silent overwrite or automatic conflict resolution. Native receive
  rejection or observed base movement yields Conflict/Rejected, preserving other writes.
- Recover Sent operations by observing/fetching the allowed ref and proving that the
  original candidate is present in its history with the original parent and operation
  trailers. Native absence, unavailable history or transport loss remains Uncertain;
  it never authorizes another push. Prepared/no-entry recovery may report authoritative
  Missing, allowing an explicit same-intent execute under fresh consumer admission.
  Terminal results are immutable and native access is rechecked before replay.
- Retain the journal state volume. Losing it is not proof that a remote effect did not
  occur; reconcile native state before resuming writes after volume loss. This adds no
  automatic retry worker or AWP lost-worker recovery. Host consumers own Workspace and
  Principal authorization, application receipts, sharing formats and policy.

## Consequences

The default standalone product and public Source workflow remain offline-capable.
No host store or relational model changes. Deterministic tests exercise actual local
Git over an authenticated loopback smart-HTTP backend, native receive races, lost
responses, restart/replay, denied paths/credentials and canonical AEP/MCP/relay routing.
These tests do not prove live hosted-Git permissions or remote service behavior.

Project sharing remains #1345; issue coordination and code delivery remain #1346/#1349.
No Console or Workplace journey is introduced.
