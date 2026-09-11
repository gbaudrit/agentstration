# ADR-0083: Git Source Provider pins and archives exact commits

## Status

Accepted — 2026-09-08

## Context

The AEP Source Provider contract separates resolving a mutable Channel selector from materializing immutable content. Git is the first transport implementation. Branches and tags may move, repository configuration can contain executable hooks, and unconstrained fetches or archives can exhaust local resources.

## Decision

Agentstration provides `Agentstration.Extensions.Git` as an autonomous AEP extension with contribution id `git` and option set `io.agentstration.git/source-channel` version `1.0.0`.

Channel options require an absolute repository and explicit `ref`. Accepted refs are full `refs/heads/*`, full `refs/tags/*`, or a 40-character commit SHA; the provider never infers a default branch. `rootPath` may select only a descendant directory. `credentialsRef` is reserved in the contract, but this first version rejects it because only public HTTPS repositories are supported. Local repositories are disabled by default and exist solely as an explicit development and offline-test option.

`Resolve` performs a bounded shallow fetch and peels the result to an immutable commit SHA. `Materialize` performs another shallow fetch of that exact SHA, verifies `FETCH_HEAD`, and invokes `git archive` against the commit or its selected descendant tree. It returns a ZIP whose compressed bytes, entry count, and expanded bytes satisfy the negotiated AEP limits.

Git runs without a shell, interactive prompts, credential helpers, global or system configuration, submodule recursion, or a usable hooks directory. Only HTTPS and explicitly enabled local-file transports reach the process. Work is bounded by cancellation, timeouts, repository bytes, archive bytes, file count, and expanded bytes. Temporary repositories and archives are deleted after each operation. Diagnostics returned to callers never contain repository command lines or Git output.

## Consequences

- A branch or tag moving between Resolve and Materialize cannot change the content used by the operation.
- Repository scripts, checkout hooks, submodules, and build steps are never executed.
- Git must be installed beside the extension process.
- Private repository authentication remains a later increment behind an explicit Secret boundary.
- The provider produces an opaque archive only; Source binding, snapshots, catalogs, Bootstrap profiles, and Packs remain owned by later Agentstration increments.
