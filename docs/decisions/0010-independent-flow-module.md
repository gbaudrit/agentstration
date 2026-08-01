# ADR-0010: Independent Flow definition module

## Decision

Implement Flow as five independent projects: Core, Contracts, Application, Storage.Abstractions, and Storage.Sqlite. The module is part of the Management Plane conceptually but does not depend on existing Management resource or storage projects.

Flow specifications use an explicit JSON discriminator and provider-neutral domain types. Mutable logical definitions use ETags; published semantic versions are immutable. Work references Flow through a lightweight optional `FlowReference`.

## Consequences

Flow can evolve and later be consumed by Runtime without creating a Management-to-Runtime dependency. A dedicated SQLite document store preserves local-first execution and readable polymorphic payloads. The additional project boundaries are intentional because the user requires Flow to remain independently packageable. FlowRun execution, scheduling, checkpoints, and MAF adaptation remain deferred.
