# ADR-0295: Workspace seeds review local service assignments

- Status: Proposed — implementation for #1347
- Date: 2026-11-27
- Owners: Bootstrap and Work Plane
- Related: #1347, #1350, ADR-0281, ADR-0285, ADR-0291, ADR-0293

## Context

A versioned seed must express the same external connection needs on independent installations.
ConnectedService, type and Tool identities, authorized scopes and credentials remain local.
The existing Bootstrap binding review and Work connection resolver already own selection and
compatibility; a separate seed provisioning or connection-discovery framework would duplicate them.

## Decision

Extend the existing Bootstrap binding kinds with ConnectedService. ProjectSharing accepts portable
connection requirements and semantic service/Describe Tool selectors. The existing configured-input
and extension-contribution preparation resolves these selectors, pins local resource identities and
validates the complete selection through WorkProjectContextService. Planned preceding resources
retain existing deferred validation, with real identities resolved at application time. Shared seed
ProjectSharing documents cannot author physical UIDs/generations, instance IDs or Workspace scopes.

The ordinary Bootstrap fingerprint includes the normalized requirements and resolved connection
proofs, including current service/type/contract/target/route evidence. Preview and application use
the same preparation path. A stale reviewed selection fails before configuration writes. The family-
neutral plan detail exposes only logical need, contract, target and local semantic reference to the
Console. Its existing administrative scope badge hides operational Workspace ownership. English
and French use the existing Bootstrap catalog. No new route or resource-screen framework is added.

After reviewed configuration, the explicit seed handover pulls sharing state. When the Project
has named requirements, absent canonical metadata is published with the approved local declarations
and the seed’s portable Project name as initial display name (never local descriptive edits).
Use a deterministic operation identity derived from the seed and local Project. Persist that identity
before publication, recover it before another write, then pull and verify the canonical requirements.
An existing incompatible declaration fails without overwrite. Retain verified metadata digest and
publication receipt separately from configuration, pull and explicitly selected draft imports.
No plan is activated, allocated or executed. Ordinary pulls never configure resources.

The unreleased YAML format 2.0 is extended in place; fresh/reseeded packages use the complete contract.
There is no compatibility conversion, database migration, new store, transport or engine. Existing
sharing-only seeds remain usable without ATC/AFC.

## Consequences

- One seed supports independent local UIDs, scopes and accounts while preserving external targets.
- Requirements and local selections are reviewable without publishing credentials or local assignments.
- Configuration may succeed while first sync or requirement publication fails; history records that
  partial outcome and recovery reuses the original operation rather than redirecting an uncertain write.
- #1350 retains its maintainer UX validation and merge gate for the reusable scenario; component
  behavior, existing regressions and desktop/mobile smoke checks belong to this implementation.
