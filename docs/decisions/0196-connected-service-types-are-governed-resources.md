# ADR-0196: Connected Service types are governed resources

## Status

Accepted

## Context

ADR-0195 introduced Connected Services over type descriptors discovered live from
Extension Registrations. That model makes the availability and identity of a type
depend on a remote inspection even though Agentstration already treats comparable
definitions, such as Flows, as durable resources. It also cannot represent a
first-party type without inventing a synthetic extension.

A Connected Service represents an external system, not the AEP extension that may
have described its contract. Its type therefore needs the same scope, ownership,
bootstrap and governance semantics as other product resources.

## Decision

Make `ConnectedServiceType` a first-class, Instance-, Tenant- or Workspace-scoped
resource. A type contains its display metadata, descendant-use policy and an
additive collection of immutable version descriptors. Each version retains its
JSON schema digest, typed/protected inputs and typed/protected exports.

A `ConnectedService` pins an exact `ConnectedServiceType` resource reference, UID,
version and schema digest. It has no direct reference to an Extension Registration
or extension identity. The type must be in the same scope or an ancestor allowed by
its use policy.

AEP `connected-service-type` contributions remain the provider-neutral wire format.
During authenticated enrollment, Agentstration materializes them as namespaced
`ConnectedServiceType` resources governed by the exact Extension Registration.
Reads and validation use those local resources and never perform live extension
inspection. Re-enrollment may add immutable versions and update mutable type
metadata, but cannot alter or remove an existing version.

Core may bootstrap first-party types with Core governance. The initial builtin is
`http-endpoint`, whose `baseUrl` input is also its exported value. It represents a
remote HTTP system and does not represent an AEP extension.

Storage/OpenDAL binding remains an explicit product-level operation. A Storage
selects a Connected Service and maps the required exports; neither extension gains
a dependency on the other.

This decision supersedes ADR-0195 only for live type discovery and the direct
Extension Registration fields in a Connected Service type reference. ADR-0195
continues to govern Connected Service ownership, late-bound credentials and manual
consumer bindings.

## Consequences

- Types can be bootstrapped, listed, governed and referenced like other resources.
- Builtin and user-authored types do not require a synthetic extension.
- Extension removal is blocked while its materialized type resources remain owned
  by that registration.
- Remote extension availability no longer affects type catalog reads.
- Existing version contracts remain stable while providers can publish additional
  versions through enrollment reconciliation.
