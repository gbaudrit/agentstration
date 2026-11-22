# ADR-0247: Correct blocked Bootstrap models through versioned plan substitutions

## Status

Accepted

## Context

An initial Model Profile can wait for a provider or model that will never become
available. Administrators need to inspect prepared resources and correct that
selection from progress without editing an unrelated global default, losing
already-created resources or deriving the Bootstrap target from the current
Console scope. Startup observations alone do not preserve an accepted choice
across process restart.

## Decision

Allow administrator consultation of resource pages during pending/failed startup.
Retain the standard Console frame with a persistent preparation notice and a link
to progress. The initial overview remains redirected to progress, and execution
and unsupported mutation endpoints remain fenced. Read exceptions preserve normal
endpoint authorization. Selecting a Console scope never changes an initial
application's target. Command search stays disabled during preparation.

Project only actual plan families beneath preparation stages. Keep summaries
small and expose bounded, administrator-only pages of per-resource operations.
These observations remain process-local and reset for a new attempt. Grouping
does not change execution order. Do not derive success from logs or replace real
operation results with a generic completion label.

For an unapplied initial Model Profile with a registered model dependency, accept
an explicit provider/model substitution through the Bootstrap application port.
Use cached provider/model inventory for form choices; opening the form never
calls an upstream provider. Evaluate discover/use against the original profile
owner, independently of Console selection. Before acceptance, validate the
provider UID, cached model availability, profile configuration and explicit
reasoning/structured-output requirements. Existing dependent-resource validators
remain authoritative during subsequent preparation.

Serialize competing changes through a version check and close the pending
selection atomically before creation. A stale form receives 409. Never replace an
already-created profile or silently discard provider-specific settings or Secret
bindings. Keep the profile's name and ownership so consumers retain their existing
references.

Persist only the previous/new exact provider and model, provider UID, version,
actor and timestamp, bound to the profile-source digest and stable resource
identity. Use atomic replacement under the existing local Data directory with
owner-only file permissions on Unix. Retain no profile body or credential value.
A changed source digest invalidates automatic replay; a replaced provider UID
requires explicit reselection. Source YAML remains unchanged. This is local
instance state, not a distributed configuration service or an export mechanism.

An accepted change during a model wait restarts resolution with a fresh bounded
dependency budget. If that model already failed, retain the initialization lease
while waiting for an explicit accepted correction and replay the existing
create-only phases in a new attempt. Deferred application workers follow attempt
changes and start once after successful readiness. Other terminal failures retain
their existing repair/restart behavior. No API request owns the initialization
execution or borrows its target from the request's scope.

## Consequences

- Administrators can diagnose a large plan without rendering its entire journal.
- Corrections survive restart and are distinguishable from source declarations.
- Operators can race safely with discovery and other operators; acceptance is not
  permission to overwrite an already-created resource.
- Startup remains offline-capable; provider availability is still determined by
  asynchronous discovery and the normal bounded dependency checks.
- The single-host/local-store and process-local observation limitations remain.
- The deferred reusable browser journey #1251 must include consultation, grouping,
  model substitution, scope isolation and retry after maintainer UX validation.
