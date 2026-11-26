# ADR-0256: Preview lifecycle binds provider-neutral requests to owned resources

Status: Accepted — 2026-11-24

## Context

Preview #1282 needs validated deploy/status/destroy entrypoints before Azure
provisioning (#1283), manual remote orchestration (#1284) and SSH transport (#1288).
ADR-0255 already establishes exact independent OCI deliveries and protected
host-side state. Both database profiles now run through the existing composition.
Environment names alone do not prove ownership of an existing Docker project.

## Decision

Keep a versioned, credential-free JSON request and safe result outside the Runtime.
The request selects exact image deliveries, environment, database, TTL, data/test
profiles and a provider-specific target. Validation describes Docker, Azure VM
and SSH/Docker targets without implementing placeholder remote adapters. Only
the Docker provider executes in this increment; unavailable execution fails
before provider mutation. Image packaging remains provider-neutral.

Reuse the current preparation, Compose and readiness implementation. Bind Docker
requests to an exact daemon and private host state to a random ownership ID.
An atomically named empty owner volume prevents competing state roots from
claiming the same project with different owners. Label and inspect every project
resource before changing it. Serialize local writers with per-environment file
locks and Actions mutations with non-cancelling environment concurrency.
Unknown resources are never silently adopted or removed.

Deploy preserves data. Reset requires explicit destroy, with owned resource IDs
and the recorded image/target selection. Stop Workers before server and database.
Keep matching credentials/state for retry; no host deletion, database conversion,
global prune or automatic schema rollback is introduced. Expiry is metadata
anchored to the first deployment; automatic TTL enforcement remains #1286.

Use one reusable/manual Actions entrypoint. Pure validation uses a hosted runner;
host operations use trusted main invocation and configured persistent Linux
runners, protected host state and read-only ephemeral GHCR credentials. Provider
transport credentials never enter requests, image metadata or Runtime services.

## Consequences

The same request can be checked before selecting a real provider implementation.
Local Docker supplies an executable vertical, with both database profiles and
safe lifecycle cleanup tested against actual containers. Azure/SSH execution
and remote URL/access policy remain their owning FRs. The contract is not a
deployment service inside Agentstration and does not add a generic adapter framework.
