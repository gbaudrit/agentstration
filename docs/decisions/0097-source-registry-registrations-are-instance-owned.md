# ADR-0097 — Source registry registrations are instance-owned policies

## Status

Accepted

## Context

The official registry consumer introduced one well-known registration, but private and community registries need independent identity, credentials, outbound authorization, refresh intent, and cache retention. Putting tokens in registry documents or host configuration would bypass Management concurrency, authorization, audit, and Secret ownership.

## Decision

- Every registry endpoint is represented by an instance-owned `SourceRegistryRegistration` with a stable UID, canonical name, display name, enabled state, explicit trust level, endpoint policy, authentication mode, Secret reference, refresh policy, and cache policy.
- Platform administrators manage registrations through one ETag-protected CRUD API. The official registration is seeded only when absent, can be changed or disabled, and cannot be deleted.
- HTTP and private-network access are denied by default. An administrator must opt in on the exact registration; link-local and multicast targets remain blocked. Redirects remain same-origin and address policy is evaluated when a connection is opened.
- Static Bearer authentication references an explicitly instance-scoped Secret whose Vault is also instance-scoped. The value is resolved immediately before each same-origin request and is never persisted in the registration, observation, error, audit, or cache.
- Deleting a non-official registration removes only desired configuration. Its immutable refresh history and existing cache bytes remain available for later provenance and retention work; imported resources are not owned by the registration.
- The refresh and cache settings are desired configuration only in this increment. Periodic scheduling, retry, cleanup, and staleness orchestration remain owned by #246 and the shared scheduler from #157.

## Consequences

Several registries can coexist without making startup network-dependent. Private access is explicit and secret-safe, while deletion does not rewrite historical evidence. Cache bytes may remain after deletion until the bounded retention mechanism is implemented by #246.
