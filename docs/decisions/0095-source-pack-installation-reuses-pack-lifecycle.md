# ADR-0095 — Source Pack installation reuses the Pack lifecycle

## Status

Accepted

## Context

A compatible pinned Source snapshot can expose Pack archives through an explicitly declared `PackCatalog`. Installing one must preserve the immutable selection and Source provenance without creating a Source-specific ownership or replacement lifecycle.

## Decision

- A Source Pack selection identifies only the Source ownership scope and identity, Source Version UID, Channel, Snapshot UID, Pack catalog, and entry name. It carries no caller-provided path, URL, digest, or moving `latest` reference.
- Preview resolves the selection server-side against the exact compatible snapshot and returns a complete installation pin containing the resolved Source version, manifest and snapshot digests, catalog and entry paths, and Pack archive digest.
- The preview digest also commits to the Pack identity and version, target scope, bindings, replacement options, and current resource and conflict states. Installation supplies that digest; the server rebuilds the same preview and rejects stale confirmation before delegating to the Pack lifecycle.
- Catalog, entry, path, Pack name, and publisher mismatches fail explicitly and are never normalized or rewritten. The current publisher policy blocks a mismatch instead of treating the Source publisher as verified Pack publisher identity.
- The selected descendant is read in place through the bounded snapshot reader and parsed by the existing Pack archive reader. The normal Pack preview, binding, target-scope, authorization, replacement, ownership, compensation, and uninstall services remain authoritative.
- Confirmation addresses the same immutable selection through the nested Source/version/Channel/snapshot/catalog/entry route. A later Channel refresh cannot move the selected snapshot or archive.
- Successful `InstalledPack` state stores Source UID/name/publisher, Source Version UID/version/digest, Channel, provider revision, Snapshot UID/digest, catalog name/path, entry, and descendant path. This nullable document field leaves local Pack installation compatible and requires no relational schema migration.
- Removing or updating a Source does not remove an installed Pack. Its retained Pack archive, managed-resource ownership, and provenance remain Pack state.

## Consequences

Source remains a discovery and distribution boundary. Local archive installation continues unchanged, and Packs discovered through Sources have the same operational semantics and safety checks as every other installed Pack.
