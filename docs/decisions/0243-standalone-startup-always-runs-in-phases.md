# ADR-0243: Standalone startup always runs in phases

## Status

Accepted

## Context

The opt-in PhasedStartupEnabled switch introduced two initialization paths. The
pre-listener path cannot accept extension announcements while Bootstrap waits for
their contributions. Selecting scoped applications also required enabling an
additional switch whose value did not express a separate product choice.

## Decision

Use one standalone startup lifecycle. Initialize storage and module schemas, open
the HTTP listener without flowing the startup System scope, then run the fenced
BootstrapStartupCoordinator. Reacquire the initialization lease on restart so
create-only profiles, dependency checks and external enrollment follow the same
sequence on fresh and existing instances.

Hold the host's ApplicationStarted notification in an IHostedLifecycleService
until Bootstrap completes or failure retains administration. The HTTP listener
is already running during that phase. Start the host concurrently, wait for the
listener lifecycle signal, run Bootstrap, then join host startup. This provides
one production lifecycle and keeps ordinary HTTP test hosts from observing a
premature ApplicationStarted signal. Enrollment tests that deliberately observe
initialization start their test host without waiting for that final notification.

Always defer ordinary application workers until readiness. Model discovery can
run before readiness. Health and proof-protected announcements are accessible
earlier; normal administrative authentication, static assets and API access become
available after topology and policy setup. Runtime execution stays fenced until
all initialization succeeds. A dependent-resource failure retains administrative
access with readiness false, as established by ADR-0242.

InitialBootstrapEnabled remains the choice to apply configured initial profiles
and scoped applications. NoBootstrap launch profiles still skip that application
without changing the host lifecycle or erasing the catalog. InitialApplications
no longer require an additional activation flag.

Remove PhasedStartupEnabled from the options contract, defaults, launch profiles,
AppHost forwarding and Compose examples. An old value left in external
configuration is ignored by the ordinary configuration binder and cannot restore
the former lifecycle. This supersedes the opt-in/default pre-listener choice in
ADR-0240; that accepted record remains historical evidence.

## Consequences

- There is one initialization path to exercise and maintain.
- Extensions can announce during any enabled startup application.
- Existing instances still preserve create-only resources and access policies.
- NoBootstrap behavior, readiness, deferred workers, cancellation and repairable
  failures must be verified alongside late enrollment and restart.
- Extension process startup and supervision remain the orchestrator's concern.
