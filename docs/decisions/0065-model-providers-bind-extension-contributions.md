# ADR-0065 — Model Providers bind registered extension contributions

## Status

Accepted

## Context

The first AEP vertical stored an endpoint and a free-form provider type directly on every Model Provider. The endpoint was also discoverable as an extension, so the same physical extension had two implicit identities. The provider type simultaneously selected a Runtime adapter and an AEP contribution. This made extension lifecycle, credentials, inventory, Packs, and future contribution evolution ambiguous.

## Decision

- `ExtensionRegistration` is the only owner of an AEP endpoint, enabled state, expected extension identity, source (`manual`, `configuration`, or `aspire`), and optional transport credential.
- `ModelProvider` owns only its display name, a typed `ResourceReference` to an `ExtensionRegistration`, and an AEP model-provider `contributionId`.
- Runtime resolution is explicit: the technical adapter is `aep`, while `contributionId` selects the contribution inside the extension. Model Profile native options remain keyed by that contribution ID and pinned to an immutable option-contract version and digest.
- The Extensions inventory starts from registrations, inspects each enabled endpoint once, and joins every Model Provider by its explicit reference. Disabling a registration makes its providers unavailable. Deletion is rejected while providers reference it.
- Configuration and Aspire endpoints are materialized as stable read-only registrations at startup. Manual registrations retain ETag CRUD. No source performs network scanning.
- Packs include Model Providers but turn their extension dependency into an installation binding. Extension endpoints and credentials are never exported.
- This is a deliberate breaking `0.x` resource-schema change. The old `providerType`, `endpoint`, `managementMode`, provider-level options, and credential fields are not read or migrated.

This decision supersedes the provider endpoint/type ownership in ADR-0018 and ADR-0026, the Model Provider endpoint export described by ADR-0052, and the merged transient inventory described by ADR-0063.

## Consequences

One extension can contribute several providers without duplicating its connection state. Endpoint or credential changes take effect for every linked provider, while provider and profile identities remain stable. Adapter selection can evolve independently from contribution IDs. Existing databases containing the old Model Provider schema must be recreated, consistent with the repository's current pre-release schema policy.
