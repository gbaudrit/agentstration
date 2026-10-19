# ADR-0086 — Source catalogs resolve inside pinned snapshots

## Status

Accepted

## Context

A Source Version declares the catalogs available through each immutable Channel snapshot. Catalog discovery must be reproducible from the pinned bytes, must not scan arbitrary repository content, and must not allow a catalog entry to address content outside the snapshot. Bootstrap content can have complete localized variants, while Pack catalogs remain independent of locale.

## Decision

- Agentstration reads only the `BootstrapCatalog` and `PackCatalog` paths explicitly declared by the pinned Source Version. It does not recursively discover manifests.
- Catalog and entry paths are relative forward-slash descendant paths. Absolute paths, empty or current/parent segments, drive-qualified paths, control characters, duplicate case-insensitive archive paths, symbolic links, and reparse points are rejected.
- Snapshot archives are inspected in place without filesystem extraction. Their actual file count and expanded size must remain within the immutable artifact metadata, and individual YAML documents are bounded.
- A catalog query returns the exact Source, Source Version, Channel, snapshot UID and digest, catalog identity, and resolved snapshot-root paths as immutable provenance.
- A Bootstrap catalog entry declares one explicit `defaultLocale` and one or more complete variants under `profiles/<bootstrap-name>/<locale>`. Locales are canonical BCP 47 identifiers or the reserved `neutral` value. Exact locale selection wins; otherwise the declared default is selected. There is no merge, inheritance, locale-chain fallback, or content negotiation.
- Every localized Bootstrap variant must retain the same profile name, target scope, and logical binding declarations. Pack catalog entries are not localized in this increment.

## Consequences

Catalog browsing is deterministic, offline, and tied to one immutable snapshot. A registry can publish autonomous translations without changing provider, Channel, snapshot, or Pack behavior. Preview, application, installation, refresh scheduling, and Console language selection remain separate increments.
