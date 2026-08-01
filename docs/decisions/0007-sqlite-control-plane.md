# ADR-0007: SQLite control-plane storage for standalone mode

## Decision

The initial control-plane store uses SQLite behind `IControlPlaneStore`. Resources are serialized as typed JSON documents with indexed type/group metadata and concurrency-token ETags.

## Consequences

Standalone operation has no server dependency. Published revisions use create-only persistence. A future relational schema can replace the adapter without changing domain resources or application services.
