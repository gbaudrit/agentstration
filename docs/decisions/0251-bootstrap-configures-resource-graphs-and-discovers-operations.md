# ADR-0251: Bootstrap configures resource graphs and discovers operations

## Status

Accepted — 2026-11-23. Owning feature: [#1279](https://github.com/gbaudrit/agentstration/issues/1279).

## Context

An installation cannot know the UIDs, generations, collection UIDs and digests
required by operation-backed Data Sources. The Nantes sample also needs a
deployment-specific PostgreSQL password. A script beside the catalogue bypasses
its preview, targeting, execution history and Console application workflow.

## Decision

Extend the existing sequential Bootstrap preparation and family handlers. Typed
`resourceRef` fields select an exact name, namespace and logical application scope.
Only earlier declarations or existing visible resources can satisfy dependencies.
Preparation resolves native revision contracts immediately before each mutation;
selectors never become business resource properties. Collection names resolve
within the explicitly selected Storage. Public extension types retain the existing
`extensionContribution` contract. Bootstrap does not acquire a second resource store
or dependency executor.

`valueFrom.configuration` is an initializer evaluated on the authoritative server.
Ordinary values live under `Agentstration:Bootstrap:Parameters`; protected values
under `Agentstration:Bootstrap:Secrets`. Secret handlers write through the existing
Vault/Secret boundary and retain configured values on retry. Preview exposes input
requirements and availability, never protected values or their hashes. Interactive
secured overrides carry only authorized Secret references created/selected through
the existing shared controls. Ordinary overrides obey the native field's supported
literal/Parameter sources. Application-local selections do not modify configuration.

AppHost forwards the effective Bootstrap subtree only to the authority. Its resolved
path and activation settings take precedence. Protected leaves use secret Aspire
parameters, without literal deployment-manifest defaults.

`ConnectedServiceOperationDiscovery` is an explicit Bootstrap instruction. Its
named outputs select exactly one eligible operation from the existing family
discovery service. Preview reads materialized metadata without synchronizing runs
or invoking a provider. Unavailable contracts produce explicit deferred validation;
local reference and input errors remain invalid. Apply uses the existing durable
Flow/AWP path, with bounded cancellation-aware polling and persisted stage/run
metadata on the existing Bootstrap application. Instance/Tenant execution uses its
governed operational Workspace while retaining logical administrative ownership.

A stable instruction idempotency key reconnects to an unfinished discovery and
reuses completed evidence. Existing consumers are create-only; changed exact
dependencies are not repinned. Operator-disabled operations are not reactivated.
Cancellation/timeout stops dependent creation and retains successful prerequisites.
No rollback or resource reconciliation is added.

## Consequences

- Nantes is an opt-in Instance catalogue profile; the offline development defaults
  remain unchanged. Extensions, destination policies, PostgreSQL and a Worker are
  external prerequisites for the real sample.
- Applying a profile discovers/configures resources. Acquisitions and Knowledge
  publication remain explicit ordinary actions.
- Bootstrap application progress remains durable and observable independently of
  the initiating HTTP connection; existing restart interruption semantics remain.
- Profile versioning/provenance remains owned by #1276. The deferred reusable UX
  scenario #1280 retains its maintainer-validation and merged-PR gate.
