# ADR-0079: Sources have immutable versioned definitions

## Status

Accepted — 2026-09-06

## Context

Bootstrap profiles and Packs can be consumed locally, but Agentstration has no durable identity for publisher-owned catalogs. A publisher may replace one stable remote `source.yaml` with a definition that adds or removes Channels, bindings, catalogs, or compatibility declarations. Existing operations still need to identify the exact definition that was imported.

The remote location is only a way to retrieve the definition. It must not imply that Git, HTTP, or another mechanism distributes Channel content.

## Decision

A `Source` is an instance-scoped, immutable identity whose portable coordinate is `publisher/name`. `metadata.name` is the readable name and the server-generated UID is the durable reference. A Source contains no repository, Channel, transport state, or mutable `activeVersion` pointer.

Publishers expose a native `kind: SourceVersion` manifest. All functional fields—including the opaque change identifier in `definition.version`, display defaults, publisher, bindings, Channels, and catalogs—are under `definition`. Agentstration retains the exact manifest text and a canonical SHA-256 digest.

Imported Source Versions are immutable and additive:

- the same declared version and canonical digest is idempotent;
- the same declared version with another digest is rejected as an inconsistent publication;
- another declared version creates a new immutable Source Version;
- older versions remain queryable even when a later definition removes a Channel.

Local presentation and observed import state are separate resources. The local display name is initialized once from the first imported version and may then be edited by an administrator. Later imports never overwrite it. Import attempts are retained, and observed state identifies the last successful import without making that version an active mutable definition.

An administrator may import one bounded YAML document directly or retrieve it from an explicit HTTP(S) URL. HTTP retrieval is bounded, timed out, streaming, and retains response validators as origin metadata. It never infers a Source Provider. Channel resolution, provider bindings, snapshots, scheduling, and catalog consumption remain separate increments.

The existing generic Management document stores persist these resource kinds in SQLite and PostgreSQL. No relational schema migration is required because no new physical column or table is introduced.

## Consequences

- A moving publisher URL does not need to retain historical files; Agentstration retains every successfully imported definition.
- Source identity survives display-name edits and publisher definition changes.
- Consumers must reference a Source Version UID rather than an implicit latest/active pointer.
- Configuration and observed state can evolve without mutating a published Source Version.
- A failed or conflicting import cannot replace previously valid Source Version data.
- The Source model stays provider-neutral and does not pre-empt the AEP Source Provider contract.
