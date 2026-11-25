# ADR-0268: HarnessProfiles publish pinned standalone native configuration

## Status

Proposed — implementation for [#960](https://github.com/gbaudrit/agentstration/issues/960).

## Context

A developer should be able to clone native instructions/configuration without running an
Agentstration SDK or service. Configuration export must be deterministic, bounded and reviewable;
profile authoring must retain the existing resource governance and ownership boundaries.

## Decision

Coding owns Workspace-scoped HarnessProfile drafts, immutable HarnessProfileVersion resources and
immutable HarnessNativeConfiguration versions. The existing generic resource stores persist these
resources with their normal authorization, optimistic draft ETags and immutable-create semantics.
There is no new database or resource lifecycle framework. Both supported stores already implement
these contracts. Publication explicitly enforces the draft's exact management authority; a manager
cannot be forged through the resource payload or bypassed by publishing another resource.

Native configurations are addressed by semantic namespace/name/version. HarnessProfile publication
captures the exact native configuration UID, generation, definition and digest internally, together
with the source profile generation and content digest. A missing or different native prerequisite
fails before export. Workspace Bootstrap handlers can plan a native configuration followed by a
profile that names its exact version; applying them resolves actual identities locally. Replay skips
an equivalent published version and preserves an operator-edited draft. Conflicting immutable
versions require explicit reconciliation. No configuration propagates automatically.

The first concrete materializer is Codex, version `1.0.0`. It emits root `AGENTS.md`,
`.codex/config.toml` and a portable provenance JSON file. Native settings permit an explicit model,
read-only/workspace-write sandbox and supported native approval choices. Shell sandbox networking
is disabled in the emitted configuration. There is no arbitrary provider URL, credential, plugin,
MCP server or automatic project-trust mutation in this slice. This is native configuration, not an
Agent deployment or an additional scheduling/runtime authority.

Instruction bindings use the existing pinned built-in JSON Schema validator. Version 1 accepts
bounded string properties, explicit maximum lengths and `additionalProperties: false`; every
placeholder must be a required declared binding. Secret references are not a binding type and no
Secret resolver is called. Instructions and binding text remain user-authored content, not product
translations. Credential-shaped binding names and unsupported native fields are rejected.

Preview/export are pure materialization plus bounded in-memory archive creation. They never write
a repository or launch the CLI. Fixed output paths, case-insensitive collision checks, path/type
validation, parent symlink/file detection and explicit ownership/digest checks prevent silent
replacement. An existing generated file is accepted only for identical owned replay. Root native
instruction overrides conflict. Broader update/reconciliation semantics remain #962/#963.

File digests and provenance include semantic profile/native configuration versions, content digests
and the exact materializer version. They exclude local Workspace/Principal IDs, local resource UIDs,
credentials and current time. Exports sort paths, normalize UTF-8/LF and fix ZIP metadata/timestamps;
two exports and a restart reproduce the same native files and archive in the same host environment.
The portable file digest describes contents independently from archive encoding.

Standalone use still requires an installed/authenticated compatible native CLI and the user's native
project trust/configuration workflow. Native user/system settings and nested instruction overrides
can affect effective execution after clone. Export pins generated content, not a claim that every
external CLI configuration source has disappeared. Local bounded execution belongs to #961.

Codex's executable reporting path is `codex exec --json`: `turn.completed` events may report usage
counters. Preview documents the known input/cached-input/output/optional reasoning-output fields;
missing fields, provider cost/currency and provider duration remain unavailable. Cached/reasoning
counts are subsets, not additional totals. #1348 owns correlated shared accounting; no metric or
price is invented here. Ordinary local deterministic Work does not require Codex or a provider key.

## Consequences

This delivers one executable authoring/publication/export vertical, preserving the existing
resource and Bootstrap boundaries. It adds no Console journey, Git write, CLI invocation or live
provider requirement. Deterministic fixtures cover ownership, traversal, schema/binding failure,
managed publication, cross-Workspace access, restart and immutable version conflicts. CLI parser
and documented event/configuration contracts can be checked separately from optional live execution.
