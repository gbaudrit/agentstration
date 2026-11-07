# ADR-0197: Connected Service operations are typed discovered resources

## Status

Accepted

## Context

ADR-0195 and ADR-0196 separate external-system declarations from Data Sources and
make their types governed resources. Operation discovery must consequently belong
to the Connected Service vertical. OpenAPI, MCP and future protocols do not share
one stable operation definition, and the OpenAPI extension must remain independent
from both the builtin HTTP type and other extensions.

## Decision

Introduce `ConnectedServiceOperationType` as an Instance-, Tenant- or
Workspace-scoped governed resource with additive immutable versions. Each version
pins a JSON definition schema, interaction shape and provider invocation contract.
Core, Bootstrap, users and authenticated AEP extensions can own these resources.

A `ConnectedServiceType` version may pin one exact discovery Flow and the exact
operation-type revisions that Flow is allowed to produce. Discovery keeps the
existing durable Flow Run model, but its input contains only exact resource
identities and caller context; it never serializes resolved Parameters or Secrets.
The Flow returns the bounded `connected-service-operation-discovery/v1` manifest.

A successful discovery validates every candidate against an allowed operation type
and materializes stable `ConnectedServiceOperation` resources. Missing candidates
become unavailable instead of being deleted. Operations are read-only through the
public API; rediscovery is their mutation boundary.

AEP exposes separate connected-service-operation-type contributions. Enrollment
provisions operation types first, bundle resources including Flows second, and
Connected Service types last so every stored dependency is an exact local pin.
OpenAPI therefore contributes its own service type, operation type and discovery
Flow without inheriting the builtin HTTP type or linking directly to another
extension.

The existing MCP Server administration remains the product's Tool Provider path.
FR-1057 uses MCP-shaped deterministic test data only and does not add a second
builtin MCP administration model.

## Consequences

- protocol-specific operation definitions remain extension-owned and versioned;
- consumers can pin stable operation UIDs while discovery evolves;
- stale Flow, type UID, version or digest references fail closed;
- discovery requires a governed operational Workspace and resource-write access;
- invocation dispatch is described by the operation type but is outside FR-1057.
