# ADR-0199: Storages own explicit Connected Service export bindings

## Status

Proposed

## Context

FR #1045 needs a selectable Storage independently from Connected Services, Data
Sources and Knowledge. ADR-0196 establishes governed types with immutable versions;
ADR-0193 keeps ASP generic and independent of product resources. No Storage product
contract has shipped. The maintainer explicitly requires direct implementation
without migrations, compatibility aliases or changes to other verticals' history.

## Decision

Introduce `Agentstration.Storage.Contracts`, `Agentstration.Storage` and
`Agentstration.Storage.Api`. Persist StorageType and Storage resources in the
existing scoped document store, with its identity, governance, audit and ETags.
There is no new relational schema or EF migration.

StorageType versions are additive and immutable. Each version describes opaque
configuration and envelope JSON schemas, typed/protected requirements, actual
capabilities, limits and optional exact extension contribution evidence, including
the provider registration UID and generation. Provider registration drift fails
closed until an explicitly selected immutable version pins the new evidence. Core types
use Core governance; extensions synchronize only resources they manage. Ownership
of a Storage type grants no authority over a Connected Service.

A Storage selects an exact type UID/version/schema/contract digest and optionally
an exact Connected Service UID/generation/type. Every binding names a requirement
and an exact export descriptor from that service type. Saving these entries is the
consumer's explicit confirmation. Names and extension ownership never generate
bindings. Local types can require no Connected Service. Configuration and the fixed
envelope are distinct opaque, bounded JSON documents. Credentials remain Secret
references on Connected Services.

Canonical routes are `/api/storage-types` and `/api/storages`, using `scopeRef`,
`namespace` and resource names. Updates, explicit reconciliation and deactivation
require `If-Match`. Reconciliation can observe drift without rebinding; accepting a
new definition requires an explicit complete replacement. Dependencies block
binding changes and deactivation. Provider loss preserves resources and evidence.

The neutral resolver revalidates scope, access policy, descendant UsePolicy, exact
identity/generation, type contract, service and manually saved exports on every
call. It returns a logical revision and a protected context with separately held
configuration/envelope. The context cannot serialize physical configuration and
its string representation is redacted. No management or Storage resolution reads
Secret values. Adapters must revalidate immediately before late secret resolution
and preserve the authority layers and actual provider-side authorization.

#1040 supplies ASP/AEP contribution and invocation adapters and the Artifact
adapter/default resource. #1045 supplies executable resource management, a builtin
local type contract and neutral provisioning/availability/resolution ports. It
does not advertise Artifact integration as completed. Existing Artifact and
acquisition paths and histories continue unchanged. #1046 owns Console UX;
#1041/#1042 own dataset/output rules and publication.

## Consequences

- Two Storages sharing a service or physical backend retain distinct resource UIDs
  and logical references; authorization is independently evaluated.
- Provider upgrades cannot silently redirect historical references. Consumers pin
  the Storage generation and binding digest and explicitly reconcile drift.
- The supported bounded JSON Schema profile is reused from the Schemas family;
  unsupported keywords fail explicitly rather than being silently ignored.
- No migration, compatibility layer, provider SDK, broker or extra process is added.
