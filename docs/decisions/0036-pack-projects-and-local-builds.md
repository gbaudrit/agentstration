# ADR-0036: Pack Projects retain sources and produce local immutable builds

Status: Accepted — 2026-08-14

## Context

ADR-0035 defines Packs as Management and distribution artifacts. Installing an archive alone is insufficient for a local development loop: an operator needs to preserve the exact input, fork it without depending on its original download location, edit Pack-level metadata, build a reproducible archive, inspect conflicts, and reinstall it without manually downloading and uploading the result.

An installed Pack is provenance, not an authoring workspace. Mutating it in place would make uninstall evidence ambiguous and erase the distinction between an observed installation and a new authored artifact.

## Decision

Every newly installed local Pack retains its validated source archive in a content-addressed artifact store. `InstalledPack` records the source hash, length, storage key, and original file name. For an installation created before source retention, the operator must attach a matching original archive before forking; Agentstration verifies its identity, version, and complete resource inventory without reinstalling resources.

A fork creates a workspace-owned `PackProject`. The project has a new Pack coordinate and editable Pack-level metadata, while its origin and immutable source artifact remain explicit. Forking does not create or mutate contained resources.

A build snapshots one project revision into an immutable `PackProjectBuild`. Archive entries have a canonical order and timestamp, the generated root manifest is canonical JSON, and unchanged project input produces identical bytes and a shared content hash. Builds can be downloaded, but local preview and installation consume the stored artifact directly through the same Pack validation and installation services as uploaded archives.

Development replacement is explicit. If the same Pack identity is already installed, the operator must request replacement; Agentstration first uses the existing modification-safe uninstall path and then performs a normal validated installation. It does not overwrite arbitrary conflicting resources.

A fork may also explicitly replace its recorded source Pack. Agentstration permits this only when every conflicting resource is managed by that installed source Pack, then applies the same modification-safe uninstall path before installing the fork. A conflict owned by any unrelated installation is rejected.

This increment installs builds only into the current request Workspace. Selecting a different Workspace is deferred until every participating resource store, including Flow and Entry storage, applies the same tenant/workspace scope. A partially scoped cross-Workspace operation is forbidden.

Flow resources carried by a Pack may include their typed graph definition and designer metadata. The graph is structurally validated before installation and is preserved in the immutable published Flow version, so it can be recreated as an editable Flow Draft.

## Consequences

- Installed provenance, editable project state, and immutable build history have separate lifecycles.
- Fork and build continue to work offline after the original archive location disappears.
- Local install never needs a browser round trip through a downloaded ZIP.
- Content-addressed storage deduplicates identical sources and builds.
- Pack-level edits are available now; contained resource editors remain owned by their existing modules and can be linked into the Pack Studio incrementally.
- Cross-Workspace build installation requires a prior storage-isolation increment for all contained resource kinds.
