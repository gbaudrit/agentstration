# ADR-0008: Reconstructible Microsoft Agent Framework runtime

## Decision

Only `Agentstration.Runtime.AgentFramework` may hold concrete `AIAgent` instances. The management plane persists resolved revisions rather than runtime objects.

## Consequences

Reconciliation can rebuild agents after restart, local and remote hosting share contracts, and Foundry remains optional.
