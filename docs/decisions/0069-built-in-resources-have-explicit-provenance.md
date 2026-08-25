# ADR-0069: Built-in resources have explicit provenance

Status: Accepted — 2026-08-24

## Context

Agentstration ships a small number of ready-to-use Management resources. Naming one of them `default` becomes ambiguous as soon as a Workspace contains several valid resources of the same kind: shipped provenance does not imply selection priority, required use, or fallback behavior.

Resource names are also user-controlled. A naming convention alone is useful for operators but is not sufficient structured provenance for APIs and future UI filtering.

## Decision

Resources shipped with Agentstration use the `-builtin` suffix. Their metadata also carries the annotation `agentstration.io/builtin: "true"` through the canonical `ResourceProvenanceAnnotations.BuiltIn` key.

The standard Microsoft Agent Framework Runtime Profile is named `maf-builtin`. New Agent definitions reference `default/maf-builtin` when no explicit Runtime Profile is supplied.

`builtin` describes origin only. It does not mean default, mandatory, immutable, trusted, or protected from deletion. The annotation is descriptive metadata and must not be used as an authorization or integrity boundary because resource metadata remains user-controlled.

## Consequences

- Operators can distinguish resources delivered out of the box from resources they created.
- New built-in resources follow one reusable naming and annotation convention across resource kinds.
- Multiple Runtime Profiles can coexist without `default` suggesting execution precedence.
- Existing `maf-default` resources and references are not renamed in place. Development installations may remove obsolete resources after moving their references; new and freshly seeded resources use `maf-builtin`.
- This decision supersedes only the `default/maf-default` fallback name recorded in ADR-0066. Its Pack Runtime Profile binding decision remains unchanged.
