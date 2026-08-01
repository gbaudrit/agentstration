# ADR-0011: Dedicated Management module

## Decision

The Management Plane is isolated from the general Domain and Application projects through two dedicated projects:

- `Agentstration.Management.Abstractions` owns the canonical resource contracts, identifiers, control-plane storage ports, lifecycle events, and Runtime-facing resolved specifications.
- `Agentstration.Management.Core` owns validation, idempotent resource use cases, generation tracking, revision compilation, and deployment orchestration.

`Agentstration.Management.Storage.Abstractions` is absorbed into `Agentstration.Management.Abstractions`. The SQLite adapter implements those ports, and Runtime consumes only provider-neutral Management abstractions. Microsoft Agent Framework remains confined to the Runtime adapter.

## Consequences

Management evolves as an explicit modular-monolith boundary. The general `Agentstration.Domain` and `Agentstration.Application` assemblies no longer contain Management code. Storage and Runtime adapters share one canonical declaration model without referencing Management behavior. This introduces two focused projects but removes the ambiguous cross-cutting Management namespaces and the storage-only abstraction project.
