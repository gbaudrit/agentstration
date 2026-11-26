# ADR-0284: Tracking documents use strict versioned YAML

- Status: Accepted — implementation for #1345
- Date: 2026-11-25
- Related: ADR-0275, #1347, #1363, #1364

## Context

The tracking repository represents one project. Maintainer decisions require YAML for portable
Agentstration documents, Bootstrap-owned configuration/Flows, and Work data under `data/`.
The initial Git sharing implementation uses JSON files and JSON byte sizes. Replacing extensions
alone would silently reset old locations and invalidate publication limits or recovery evidence.

## Decision

Sharing format 2.0 uses one YAML index and immutable version/digest-addressed YAML records.
The existing Work services, protected Git boundary, durable native attempts, CAS and local execution
authority remain. Work/Application depend on a document-codec port; Infrastructure implements it
through the existing ResourceManagement YAML serializer. No YAML/provider dependency enters Work.

The portable reader supports one JSON-compatible YAML document with strict typed contracts,
unique string keys, no explicit tags/anchors/aliases, bounded depth and valid UTF-8. Scalars preserve
integer/decimal precision and quoted/literal text. Family normalization determines business-content
identity, independent of YAML property ordering. Transport sizes measure emitted YAML bytes; exact
Git revisions/archive hashes remain separate integrity evidence. HTTP and private storage keep
their existing formats.

The tracking connector selects a bounded logical location, conventionally `data`. Bootstrap
configuration, including Flow definitions, remains outside this Work location under `bootstrap/`.
Later authored Work imports and organization reuse these boundaries rather than introducing a
Scenario resource, another Project level or a scheduler.

No automatic dual-read or migration is introduced. Both participants explicitly select format 2.0.
An existing JSON index at the chosen location is an upgrade-required error, never an empty space.
Operators retain history and review either a new location or an explicit complete conversion.
Uncertain native attempts retain their original format, connection and bytes; they are not rebased.

## Consequences

The existing offline Git/API fixtures exercise real YAML transport, independent stores, recovery,
typed content and legacy-index rejection. Both participants must upgrade together. This decision
extends the initial representation in ADR-0275 without rewriting its historical contract.
