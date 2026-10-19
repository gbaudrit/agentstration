# ADR-0136 — Model specifications are typed and resolved deterministically

Status: Accepted — 2026-09-22

## Context

Model discovery currently exposes an open list of capability names and Runtime maps that list into a small effective-capability intersection. The list cannot describe input and output content types, feature modes, reasoning efforts, structured-output constraints or quantitative limits. It also treats omitted discovery metadata as unsupported, although omission may mean that the provider does not know or report the capability.

Provider-discovered Model resources and administrator corrections need one provider-neutral contract before persistence, AEP transport or Console workflows are introduced. The contract must distinguish what a model can do from what a Model Profile or Runtime Profile requests for an execution.

## Decision

- `ModelSpecification` describes observed input and output content types, typed feature objects and quantitative limits. Initial content types are text, image and audio. Initial features are streaming, Tools, structured output and reasoning.
- Feature support is one of `unknown`, `unsupported`, `native`, `emulated` or `partial`. Missing observations remain unknown. Explicit unsupported support is distinct and cannot be elevated by an administrative override.
- Feature details are typed maps. Tool modes, structured-output formats and reasoning efforts can gain additive properties without replacing features with untyped dictionaries.
- `ModelSpecificationOverride` is a value object, not a Resource. It uses explicit `add` and `remove` operations for collections. Missing override properties inherit the observation, and removal wins when the same value is both added and removed.
- Overrides may supply an unknown limit or restrict a known maximum. They cannot increase a known maximum.
- `EffectiveModelSpecificationResolver` is a pure deterministic function. It does not persist its result and does not perform provider, storage, authorization or network work.
- Model Profile generation, reasoning and output values remain requested configuration. Runtime Profile execution defaults remain Runtime configuration. Neither is copied into the Model specification.

## Consequences

Later AEP, persistence and Runtime increments share one typed vocabulary and merge behavior. Provider-specific discovery fields must be translated at extension boundaries. Unknown support fails closed when a later execution requires it, while an authorized provider-owned override may complete an unknown observation. Bootstrap, Packs, Model persistence and Console administration remain outside this decision and require their own feature increments.
