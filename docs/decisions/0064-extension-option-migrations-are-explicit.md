# ADR-0064 — Extension option migrations are explicit

## Status

Accepted

## Context

Versioned option contracts prevent a new extension release from silently reinterpreting persisted Model Profile values. They deliberately leave an operator-visible incompatibility when an old contract must be retired. Agentstration cannot safely invent a transformation because the extension owns the meaning of provider-native fields, while accepting transformed JSON from the Console would bypass the extension boundary and target-schema validation.

## Decision

- An AEP extension may register directed migrations between versions of one option set. The configuration catalog publishes those edges.
- The AEP server finds a migration path to the requested target version, executes each edge in order, and validates values against the source schema and every intermediate and target schema. The option-set identity cannot change.
- Agentstration sends only persisted source values and the requested target version. It independently verifies the source and returned target version, schema digest, and values against the live catalog.
- Preview is read-only and returns the current and proposed envelopes with the current Model Profile ETag. Apply reruns the migration from current persisted state and updates the complete Model Profile only with `If-Match`.
- A failed, unavailable, invalid, or concurrent migration leaves the persisted profile unchanged. Migration is never automatic during extension discovery, profile resolution, or runtime execution.
- The Console offers migration only when the catalog contains a path from the pinned version to the preferred version and requires an explicit confirmation after displaying both envelopes.

## Consequences

Extensions retain control of semantic transformations while Agentstration retains control of persistence and concurrency. Multi-step migrations allow a profile to skip intermediate releases without trusting unvalidated intermediate values. Extension authors must keep the source and target schemas published for every supported migration path. Legacy unversioned values still require a separately authored import strategy because they have no verifiable source contract.
