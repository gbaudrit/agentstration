# ADR-0194 — Model Provider bindings support typed literals

## Status

Proposed — implementation for #1051, including the maintainer-requested Literal source.

## Context

The reusable Console value-binding editor must also let administrators enter ordinary configuration directly without creating a Parameter or Secret. The existing persisted Model Provider binding only represents exact references. A UI-only literal would be lost when saving or would require creating an unexpected resource.

## Decision

- Extend `ModelProviderValueBinding` with an additive `literal` kind carrying one JSON scalar in `literal`. A binding has exactly one payload: Parameter reference, Secret reference or literal. Existing kinds and exact-reference semantics remain unchanged.
- Validate literals against the contribution requirement using the existing AEP bound-value validator, including scalar type, allowed values, the 64 KiB inline limit and protection. Secured requirements require Secrets. Validation errors and diagnostic string representations do not echo literal values.
- Persist ordinary literal configuration in the Model Provider resource JSON. It is visible through authorized resource administration and export, like Parameter configuration; it is not a credential store. No relational migration or AEP wire-contract change is needed.
- At discovery, capability resolution, chat and streaming, map literals to AEP inline bound values. Parameter resolution and Secret one-use capability issuance retain their existing paths. Model Profile Secret overrides retain their current precedence and validation.
- Keep the shared Console editor's requirement, option and selection models independent of consuming resource-family contracts. The consumer owns compatibility filtering and conversion to its persisted bindings.

## Consequences

Administrators can configure ordinary typed values without additional resources, while Parameters remain reusable and Secrets remain write-only. Existing readers that do not recognize the new binding kind need an upgrade before administering a resource containing literals. Existing resources require no conversion. The deferred UX follow-up #1052 starts only after explicit maintainer UX validation and merge; existing regression checks remain required in the implementation change.
