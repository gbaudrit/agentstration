# ADR-0030: AEP is an autonomous SDK and Inspector repository

## Status

Accepted — 2026-08-11

## Context

AEP began as projects inside Agentstration. Although its core assemblies had neutral dependencies, repository placement, tests, versioning, and discovery vocabulary still presented it as an internal API. Extension developers need to build and validate an extension without cloning or running Agentstration, and an Inspector must infer all behavior from the protocol rather than product or provider knowledge.

## Decision

- The complete AEP workspace is staged under the self-contained `aep/` directory for extraction into a dedicated repository.
- AEP owns its contracts, canonical client, ASP.NET Core server SDK, validation library, optional Microsoft.Extensions.AI integration, CLI, generic samples, conformance tests, and standalone Blazor Inspector.
- Dependency direction is strictly Agentstration to AEP. AEP projects do not reference Management, Runtime, Infrastructure, Web, Workplace, or official extensions.
- Protocol version `2026-08-01` uses `GET /.well-known/aep` and an extensible map of independently versioned capabilities. The former discovery route remains a temporary compatibility alias.
- `Agentstration.Aep.Client` is the sole protocol client used by validation and Inspector. Its optional HTTP tracing redacts sensitive headers and JSON fields.
- The transition build uses local project references by default. Setting `UseLocalAepProjects=false` switches Agentstration consumers to centrally versioned AEP packages after those packages are published.
- Official Ollama and Utilities extensions remain in Agentstration for now and consume AEP; they are not part of the future AEP repository.

## Consequences

The `aep/` subtree can be moved with its own solution, SDK selection, central packages, licence, documentation, tests, sample, CLI, and Inspector. Protocol, SDK, extension, and Agentstration versions can evolve independently. A future capability can be added without changing a fixed global supports-flags DTO. The compatibility alias and local project-reference mode are transitional and may be removed only after consumers have migrated to published packages.

This decision supersedes ADR-0026 where it treats AEP version `1.0` and `/.well-known/agentstration` as the permanent protocol identity; its out-of-process extension and provider-neutral dependency decisions remain accepted.
