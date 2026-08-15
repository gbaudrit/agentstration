# ADR-0039: Pack manifests use the native definition envelope

Status: Accepted — 2026-08-15

## Context

ADR-0031 establishes `metadata` and a typed `definition` as the canonical Agentstration manifest envelope. The first executable Pack contract used `metadata` and `spec`, even though Pack resources are authored, serialized, and inspected alongside other Agentstration manifests. That exception made the format harder to learn and suggested a semantic distinction that the Pack lifecycle does not require.

ADR-0037 still applies: a Pack is a Management and distribution artifact, not a runtime-capable resource. Its distribution role does not require a different top-level envelope.

## Decision

Pack manifests use `apiVersion`, `kind`, `metadata`, and a required typed `definition`. Pack identity and presentation fields remain in `metadata`; contained resource paths and dependency requirements belong to `definition`.

The former `spec` shape is not retained as a compatibility alias. Pack support has not yet shipped as a stable external contract, and silently accepting `spec` as an empty definition could produce an incomplete installation. Importers therefore reject manifests that omit `definition`.

## Consequences

- Pack and contained-resource manifests share the same top-level Agentstration vocabulary.
- Pack authoring builds emit `definition`, regardless of whether the original archive used YAML or JSON.
- Existing development Pack archives using `spec` must rename that property to `definition` before import.
- Pack execution semantics do not change; resources and requirements keep their existing meaning.
