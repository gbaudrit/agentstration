# ADR-0006: Agentstration is the independent management plane

## Decision

Agentstration owns stable identifiers, definitions, versions, immutable revisions, deployments, and desired state. Runtime providers, including Microsoft Foundry, are adapters and never the source of truth.

## Consequences

Runtime objects are reconstructible. Provider bindings are external metadata. Management and runtime storage can evolve independently.
