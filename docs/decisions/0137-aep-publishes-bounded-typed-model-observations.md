# ADR-0137 — AEP publishes bounded typed Model observations

Status: Accepted — 2026-09-22

## Context

AEP model discovery returned open string capability lists and provider-defined string metadata. This shape could not preserve partial knowledge, typed feature details or limits, and the Agentstration host interpreted every omitted capability as unsupported. Provider metadata such as Foundry capability keys also leaked beyond its extension boundary.

AEP is an autonomous protocol and SDK that must remain independent of the Agentstration product assemblies. It therefore cannot reference the canonical `Agentstration.Models` domain directly.

## Decision

- AEP protocol `2026-09-22` defines `aep.model-provider` capability version `1.0` with `AepModelSpecification` and `AepModelIdentity`. The earlier string capability lists and metadata bag are removed without advancing the capability version while AEP remains pre-stable.
- The AEP specification mirrors the provider-neutral vocabulary needed on the wire: input and output content types, typed streaming, Tool, structured-output and reasoning observations, support state and token limits. It contains no override or effective-resolution behavior.
- Missing observations and explicit `unknown` remain distinct from `unsupported`. Provider adapters translate their native discovery payloads at their own boundary. Foundry boolean `true`, `false` and omission map to `native`, `unsupported` and `unknown` respectively.
- Publisher, model and version identity values are optional, bounded and safe strings. Provider-specific diagnostic metadata is not transported as Model identity.
- The server SDK validates provider output before serialization and the canonical client validates it again after deserialization. Model count, strings, collections, uniqueness, limits and unsupported-feature details are bounded.
- The Agentstration AEP adapter explicitly maps the autonomous wire DTO into `ModelSpecification`; it does not expose AEP contracts from Models APIs.

## Consequences

Extensions can publish incomplete observations without inventing negative support, and the host receives one typed model view across Foundry, Ollama, llama.cpp and LocalAI. The wire change is intentionally breaking between alpha revisions, but the pre-stable model-provider capability remains version `1.0` and no compatibility alias is retained. Persisting or reconciling discovered Model resources remains a separate increment.
