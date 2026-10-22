# ADR-0170: Schema format resolution is explicit

## Status

Accepted

## Context

ADR-0169 makes Schema resources format-neutral and requires the standalone product to understand its first JSON Schema format without an AEP extension. Later AEP contributions may provide other formats, or an alternative provider for an existing format. Selecting the first registered provider would make behavior depend on registration order and could silently change validation after an extension is installed.

## Decision

The Schemas family owns a format registry containing provider identity, provider version, supported dialects, capabilities, availability and an explicit default marker. Operations resolve a required capability such as definition validation, instance validation, visual authoring or instance preview. A caller may pin an exact provider identity. Without a pin, resolution succeeds only when there is one capable provider or exactly one explicitly designated default; ambiguous registrations fail.

The standalone composition registers `builtin/json-schema@1.0` directly. It supports the 2020-12 dialect for the bounded keyword subset documented with the Schema concept. Definition and instance validation return a common result containing path-specific issues. Schema draft writes use the same definition-validation capability, while read and immutable-version inspection remain independent from provider availability.

AEP is not part of the built-in handler's dependency path. A later AEP adapter may register handlers through the same interface and reuse governed MCP transport operations, but it must preserve the registry's identity, capability, availability and ambiguity rules.

## Consequences

- JSON Schema authoring and validation work in the local standalone product without an extension process or MCP round trip.
- Adding a provider cannot silently replace another provider for the same format.
- Unavailable providers and missing capabilities produce explicit diagnostics.
- Format-specific validation stays outside the format-neutral Schema resource contract and storage model.
- The first handler intentionally rejects unsupported keywords rather than pretending to validate them.
