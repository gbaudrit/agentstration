# ADR-0123 — AEP Value Requirements may constrain invariant allowed values

Status: Accepted

## Context

Some extension configuration values are a closed set of technical choices rather than arbitrary scalars. Treating values such as an authentication mode as free text weakens authoring guidance and delays errors until provider-specific code runs. The constraint belongs to the contribution contract, not to an installation Parameter that may be reused by other consumers.

The `aep.value-requirements` capability introduced by ADR-0122 has not shipped. Its version can therefore remain `1.0` while the initial contract is completed.

## Decision

- A standard `AepValueRequirement` may declare optional `allowedValues` containing typed JSON scalars compatible with its declared `type`.
- Absence means unconstrained. A present list contains 1–64 unique values and is bounded to 65,536 serialized bytes. Strings use ordinal, case-sensitive comparison.
- Secured requirements cannot declare allowed values. A constrained standard requirement must be supplied inline and cannot use a Secret grant, so validation never requires reading Secret material.
- Descriptor validation rejects empty, excessive, oversized, duplicate, nonscalar, or wrong-type declarations.
- Bound-value validation rejects a value outside the declared set before contribution invocation. Agentstration repeats the same validation when saving Model Provider bindings and at every runtime resolution so later Parameter changes fail closed.
- Errors identify the requirement and stable error code but never include the rejected value.
- The constraint remains part of `aep.value-requirements` version `1.0`; no migration or compatibility shim is introduced for the unreleased contract.

## Consequences

Extensions can publish provider-neutral enumerated configuration choices. Hosts can render constrained authoring controls while retaining server-side enforcement. Parameters remain generic scoped resources and do not persist consumer-specific schemas. Conditional requirements, localized option labels, and Secret enumeration remain separate future concerns.
