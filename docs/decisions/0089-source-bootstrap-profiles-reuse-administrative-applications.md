# ADR-0089 — Source Bootstrap profiles reuse administrative applications

## Status

Accepted

## Context

A pinned Source snapshot can expose localized Bootstrap Profile variants through a declared catalog. Applying that content needs the existing target, binding, preview, conflict, execution, audit, and history semantics. A Source-specific provisioning lifecycle would duplicate those rules and could silently drift from local Bootstrap behavior.

## Decision

- A Source Bootstrap selection pins its ownership scope, publisher and Source name, Source Version UID, Channel, Channel Snapshot UID, Bootstrap catalog and entry, exact selected locale, and exact descendant variant path.
- The selected compatible catalog entry is resolved again for preview and application. Its locale and descendant path must still match the pin exactly; local profiles and Source profiles cannot be composed in one application.
- Only files below the pinned variant directory are materialized into a bounded temporary profile directory. The existing Bootstrap profile parser and handlers then own descriptor validation, bindings, target scope, planning, conflicts, application, and Pack handling. Other locale variants are never loaded or merged.
- Preview digests cover the complete Source provenance in addition to the parsed profile digest, target, and bindings. Changing the locale, path, version, Channel, snapshot, catalog, or entry invalidates confirmation.
- Successful `BootstrapApplication` history stores Source UID and identity, Source Version UID/version/manifest digest, Channel, provider revision, Snapshot UID/digest, catalog identity/path, entry, locale, and descendant path. This nullable document field is backward-compatible with existing local application history and needs no relational schema migration.
- Source, version, snapshot, and publisher identities are checked without normalization or rewriting. Any mismatch is an explicit invalid selection.
- Temporary materialization is deleted after preview or application. Provisioned resources remain ordinary Bootstrap-created resources and are not deleted when their distributing Source changes or is removed.

## Consequences

Source distribution gains no second provisioning engine. Local Bootstrap behavior remains unchanged, Source applications are reproducible from an immutable snapshot, and the same application service is available to HTTP and Console callers. Binary descendants such as Pack archives remain usable because the complete selected variant is materialized before the existing handlers run.
