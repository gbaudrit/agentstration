# Management Plane

The Management Plane is the source of truth for desired state. It owns canonical resource declarations, validation, generations, immutable agent revisions, deployments, provisioning status, resource identifiers, concurrency metadata, and lifecycle events.

It does not persist concrete Microsoft Agent Framework `AIAgent` instances. Runtime objects are reconstructible from governed resources. The canonical abstractions live in `Agentstration.Management.Abstractions`; use cases and validation live in `Agentstration.Management.Core`.

See the [Management Plane implementation note](../management-plane.md) and [ADR-0006](../decisions/0006-independent-management-plane.md).
