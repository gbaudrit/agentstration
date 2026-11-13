# ADR-0217: Structured Snapshots pin immutable document publications

## Status

Proposed

## Context

[#1155](https://github.com/gbaudrit/agentstration/issues/1155) composes acquired JSON, normalization Flows and native Knowledge access. ADR-0208 requires produced content to remain an Artifact; exact historical reads must never follow the active Snapshot or current output Storage.

## Decision

A Knowledge output explicitly selects a document collection on an exact Storage. A projection gathers validated JSON objects and prepares one immutable publication, seals it, then catalogs canonical JSON Artifact materializations before publishing the Snapshot. Canonical bytes sort object properties and encode supported decimal values deterministically; they do not promise preservation of original JSON whitespace or member order. Provenance remains attached to the projection and acquisition evidence.

Each Artifact reference records its exact document ID, revision, collection, publication digest, model and query profile. The Artifact identity includes the collection and publication to avoid collisions across Snapshots. Existing content-backed Snapshots keep the original path. Native read and regex use the same Artifact publication boundary; structured query uses only the exact publication recorded in the Snapshot.

Query selection, query-first preparation, freshness and expiry fallback reuse native Knowledge access. Protected cursors fix the Snapshot UID, caller, scope, profile and original query and reuse retention leases. New publication does not alter an existing cursor. Current authorization is rechecked. The query relay fixes source identity and the effective profile/model and exposes no Knowledge write/delete/publication operations. Tool exposure is explicitly versioned and depends on the selected Storage capabilities and policy. Publishing new output capabilities does not rewrite old Tool versions.

## Consequences

Snapshots retain atomic consumer visibility, history, provenance and exact references. Retirement respects active read/query leases and accepted executions; retained publication identity participates in sharing checks. Loss of an old provider, model or Storage revision fails explicitly, with no redirection. Structured Knowledge can be composed with acquisition changes from #1115/#1117 and native access from #1119 without changing their responsibilities.
