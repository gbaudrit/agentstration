# ADR-0062 — Extension options use immutable versioned contracts

## Status

Accepted

## Context

Model Profiles persist provider-native options while provider implementations evolve outside the Agentstration process. A raw JSON object gives neither the runtime nor an operator enough information to distinguish a compatible additive change from a removed or reinterpreted field. An external extension can also be upgraded independently, so Agentstration cannot make process replacement atomic with its own Management data.

## Decision

- An AEP extension publishes its option contracts through the versioned `aep.configuration` capability. Each option set identifies its contribution and scope and contains an exact preferred version plus every still-supported version.
- A persisted native option value is an envelope containing `optionSet`, exact `version`, `schemaDigest`, and `values`. The preferred version is an authoring hint only; it never silently upgrades an existing Model Profile.
- An option-set version is immutable. Changing its schema requires a new version. The SHA-256 schema digest detects an extension that reuses a version while changing its meaning.
- An extension remains backward compatible by continuing to publish and execute every version referenced by persisted resources. It may add a newer preferred version without changing existing executions. Migration to a newer version is a separate explicit Management operation.
- Agentstration and the AEP server validate the option-set identity, version, digest, and JSON Schema subset before provider invocation. Unknown top-level provider fields are rejected unless the contract exposes an explicit extension bag such as `additionalOptions`.
- If an independently upgraded extension removes a pinned contract, Agentstration cannot prevent that external replacement. It detects the incompatibility, exposes it on the Extensions API and Console page, and fails closed before invoking the provider.
- The initial Extensions Console is an operational inventory derived from workspace Model Provider declarations and endpoints explicitly registered under `Agentstration:Extensions`, including Aspire-injected endpoints. It performs AEP discovery on those known endpoints and never scans the local network. It shows live extension identity, contributions, published contracts, pinned Model Profile usages, and incompatibilities; an unconfigured model-provider contribution can prefill the existing Model Provider creation form. Installation, upgrade orchestration, and automatic option migration are separate future decisions.

## Consequences

Existing Model Profiles no longer inherit a new interpretation merely because an extension changes. Extension authors must retain old contract implementations for a compatibility window and publish a new version for schema changes. Persisted envelopes are more verbose, but they are self-describing and diagnosable while retaining raw provider-native values. Updating an external extension remains an operator action; the platform guarantees detection and safe refusal, not control over a process it does not own.
