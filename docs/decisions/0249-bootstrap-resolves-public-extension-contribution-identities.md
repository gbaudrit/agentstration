# ADR-0249: Bootstrap resolves public extension and contribution identities

## Status

Accepted

## Context

Portable Bootstrap manifests cannot know the locally assigned registration name
or the scoped addresses produced by enrollment. A public extension identity also
does not identify a particular contribution or disambiguate two installations.

## Decision

Resource handlers declare their typed extension-reference paths. At these paths,
Bootstrap accepts `{ id, instanceId? }` instead of a name-based registration
reference. Structured `extensionContribution` placeholders and typed profile
bindings with `extensionTarget` identify an extension, optional installation,
contribution kind, public contribution ID and optional version.

Preparation collects only consumed extension bindings and direct selectors.
Resolution is read-only, uses visible enabled registrations, and rejects ambiguous
installations. Existing enrollment records must be Available. A manually trusted
registration can resolve by its expected extension identity without inventing an
installation ID. Each contribution adapter verifies exact registration provenance,
resource-family availability and the selected version. Tools, Connected Service
Types, Storage Types and installed Data Source Profiles use their existing stores
and native identities; no independent contribution resource catalog is introduced.

Resolved references retain scope and namespace. Typed service/storage references
also include their existing UID, version and digest pins. Reference fingerprints
participate in preview and execution digests so substitution at the same local
name cannot silently replace the reviewed resource. Preview never enrolls or
provisions an extension. Public contribution bindings are authored selectors;
they cannot be overridden with an unqualified local-name binding selection.

Existing name references and ordinary typed bindings remain supported. Automatic
extension bindings can be declared at Instance, Tenant or Workspace profile scope;
ordinary interactive bindings retain their existing Workspace workflow. Pack
binding targets are resolved before delegation to the existing Pack boundary.

## Consequences

- Bootstrap-derived requirements can drive a later startup dependency barrier.
- New family adapters can resolve additional contributions without changing the
  generic resource model or moving family ownership into Bootstrap.
- An announced identity is insufficient for contribution readiness.
- Extension launching and implicit scope promotion remain outside this workflow.
