# ADR-0189: Service registration has explicit module ownership

## Status

Accepted — 2026-11-04

## Context

[#240](https://github.com/gbaudrit/agentstration/issues/240) identifies a growing central registration method, duplicated singleton contracts, and model selection that depends on a later host registration. The host and resource-family boundaries have evolved since the issue's original evidence. Splitting files or adding regions would leave those dependencies implicit.

## Decision

- Keep explicit `IServiceCollection` extension methods at cohesive ownership boundaries. The implementation-owning module owns its registrations when that preserves dependency direction; Infrastructure owns cross-module adapters. Internal vertical extensions remain internal unless another project has a concrete composition need.
- Keep `AddAgentstration` as a compatibility façade over validated composition options and an ordered sequence of named vertical extensions. Preserve the existing overload for repository and external callers. Select the storage provider once and delegate concrete database registrations to the existing module-owned storage extensions.
- Keep executable host extensions responsible for presentation and transport, identity integration, bootstrap hosting, realtime projections, background workers, test cleanup, and observability. Keep endpoint mapping, database initialization, bootstrap execution, and application startup outside registration methods.
- Give exactly-one contracts one descriptor. Use `TryAdd*` for genuine defaults, explicit replacement for a selected production implementation, and `Add*` for intentional enumerable contributions. Document the remaining significant ordering, including scheduler startup and ordered handlers, in the [registration ownership map](../architecture/implementation/service-registration.md).
- Keep the convenient standalone and Console façades while splitting their bounded responsibilities. Registration is explicit; do not introduce assembly scanning, another container, or application-level service location.
- Inspect descriptors before building the provider, then validate the complete graph with scope and build validation. Cover supported AI/storage selections and enabled/disabled background hosting in registration-contract tests.

## Consequences

Reviewers can locate registration ownership without tracing a monolithic method or relying on last-registration-wins behavior. Descriptor tests expose duplicates that successful service resolution would otherwise hide. The options façade reduces parameter coupling while preserving source compatibility.

This changes composition structure and makes selection contracts explicit. It does not change service lifetimes, resource ownership, schemas, supported deployment topology, local SQLite/deterministic defaults, or the ordered initialization lifecycle. The authoritative server and independent Console/Workplace boundaries established by earlier ADRs remain in force.
