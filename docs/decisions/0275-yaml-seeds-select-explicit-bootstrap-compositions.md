# ADR-0275: YAML seeds select explicit Bootstrap compositions

- Status: Accepted — implementation for #1347
- Date: 2026-11-25
- Related: ADR-0271, ADR-0274, #1276, #1363

## Context

The initial Workspace seed format requires JSON manifests/pointers and only applies flat Bootstrap
files. The tracking convention requires YAML documents, Bootstrap-owned Flow/configuration trees
and a separate Work `data/` directory. Recursive application of every YAML file would accidentally
apply auxiliary declarations and make directory placement determine execution order.

## Decision

Seed format 2.0 contains `agentstration-workspace-seed.yaml` and accepts a strict YAML pointer,
including public HTTPS `.yaml`/`.yml` URLs. Existing byte bounds, authorization, Source archive/network
policy, exact artifact evidence and Workspace-only review/application/handover remain authoritative.
Both manifest and pointer use the strict JSON-compatible YAML subset from ADR-0274.

A Bootstrap descriptor may declare `definition.resources`, an ordered explicit list of relative
manifest paths inside the validated profile. The loader resolves only that selection, retaining
original nested file locations for review and provenance. Duplicate/missing/unsafe paths, nested
descriptors and duplicate resource identities are invalid. Existing flat profiles without a list
keep their root lexical order. Complete validated profile files still enter its content digest.

Configuration and Flow definitions stay under `bootstrap/`. Work data does not become configuration
because it shares the archive: initial pull and selected draft Work import remain explicit handover
operations. Ordinary synchronization never applies Bootstrap or launches work.

The format change requires regeneration/review of old seed packages. It does not silently reinterpret
format-1.0 JSON artifacts or grant upgrade/reconciliation authority. Declared seed version, exact
provider revision and byte/profile digests keep their distinct meanings. Full profile-version/history
and upgrade capabilities remain with their existing owning features.

## Consequences

The same family handlers, target/input checks, application worker and durable receipts execute the
composition. English/French seed guidance and source inspection describe YAML and exact selected
files. Offline fixtures cover complete/pointer equivalence, nested selection, tampering and denial;
the existing #1350 UX follow-up retains its validation/merge gate.
