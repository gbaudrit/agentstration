# ADR-0102 — Source Providers follow hierarchical resource visibility

## Status

Accepted — 2026-09-10

## Context

ADR-0081 initially made every Source Provider and its Extension Registration instance-owned. Since then, ADR-0079 introduced explicit hierarchical ownership and ADR-0090 established that manually registered extensions are normally tenant-owned while configuration and Aspire registrations remain instance-owned. Keeping Source Providers instance-only prevents a tenant-owned AEP extension from contributing a Source Provider even though Sources themselves may be owned by an instance, tenant, or workspace.

## Decision

- A `SourceProvider` may be owned by an instance, tenant, or workspace scope.
- Its `ExtensionRegistration` reference is exact and may target the same scope or an ancestor visible from the Source Provider scope. Sibling and descendant references remain invalid.
- A Source binding stores the exact Source Provider scope after resolution. A Source may therefore select a provider from its own scope or an ancestor, but never from a sibling or descendant scope.
- Console and HTTP identities include `scopeRef` whenever a Source Provider is addressed. The ownership scope remains immutable after creation.
- Configuring a provider from an observed extension contribution initially selects the extension registration's scope. Administrators may explicitly choose a descendant scope when configuring the provider directly.
- The Extension Registration remains the sole owner of its endpoint, enabled state, extension identity, and transport credentials. Channel selectors and locale remain Source configuration.

## Consequences

Tenant-owned manual extensions can provide Source acquisition without being promoted to the instance. Instance extensions remain reusable by tenant and workspace Sources, while tenant extensions remain reusable by their workspaces. Exact scoped references prevent ambiguity when the same namespace and provider name exist at several hierarchy levels. This decision supersedes only the instance-ownership restrictions in ADR-0081 and ADR-0084; their AEP contribution, pinning, and binding-separation decisions remain unchanged.
