# ADR-0084 — Source Provider bindings are local version-aware configuration

## Status

Accepted

The instance-ownership restriction is superseded by [ADR-0102](0102-source-providers-follow-hierarchical-resource-visibility.md).

## Context

A portable Source Version must name the acquisition role used by each Channel without embedding an installation-specific Source Provider resource. The local provider can be disabled, unavailable, removed, or expose option contracts that differ from those required by a particular immutable Source Version. Imports must not silently select a provider or overwrite an administrator's existing choice.

## Decision

- Binding declarations and Channel references remain immutable members of the published Source Version and therefore participate in its manifest digest.
- Concrete binding selections are stored on the mutable, same-scope `SourceConfiguration` resource. Each selection retains the logical binding name, declared target kind, and an exact instance-owned Source Provider reference. Selections never participate in the published manifest digest.
- Configuration is evaluated against an explicit Source Version UID. Compatible selections are reused across imported versions. A removed or target-kind-changed declaration does not consume the old selection; the API reports it as stale so it can be inspected without silently discarding potentially reusable local configuration.
- Updating selections is an ETag-protected Management write that replaces the selections for the requested version's compatible declarations while retaining unrelated stale selections. Duplicate, undeclared, wrong-kind, and missing-provider selections are rejected with stable validation codes. Omitting a declaration clears its compatible selection and leaves it unresolved; Agentstration never chooses a provider automatically.
- Status inspection resolves the Source Provider and its Extension Registration, verifies the advertised `source-provider` contribution, and validates every consuming Channel against an exact `source-channel` option-set id, version, schema digest, and JSON schema. Operational unavailability and contract incompatibility remain saved, observable states rather than destructive configuration failures.
- `GET` and `PUT /api/sources/{publisher}/{name}/versions/{versionUid}/bindings` expose the version-aware status and configuration to Platform administrators. Source ownership remains instance, tenant, or workspace; provider ownership remains instance-only and normal ancestor visibility rules apply.

## Consequences

Published Source YAML remains portable while each installation controls acquisition. A provider choice can survive additive Source Version imports without creating an implicit active version. Snapshot creation can require a ready binding status in a later increment. Administrators receive actionable unresolved, missing, unavailable, and incompatible states, while stale selections remain explicit and harmless.

Locale selection is independent of provider binding. Localization variants introduced by later Source features do not alter the provider resource, Channel transport configuration, or binding selection.
