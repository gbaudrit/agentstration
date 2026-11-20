# ADR-0244: Observe startup through an administrator-only progress screen

## Status

Accepted

## Context

The always-phased listener accepts login before Bootstrap completes. Routing an
authenticated visitor to the ordinary overview at that point exposes unavailable
sources instead of the ongoing initialization. ADR-0242 retained authenticated
administration for repair, but did not restrict admission to global Platform
administrators during this phase. The maintainer requests that temporary rule
and authorizes the first progress-page integration in #1250.

## Decision

Keep the existing anonymous waiting page and empty login-availability probe.
After authentication, route browser entry and ordinary Console navigation to
`/startup/progress` while startup is pending or failed. Use the shared StepProgress
component under a small authenticated layout, independent of the overview. Permit
explicit provider/profile/extension repair navigation for admitted administrators.
At readiness, restore ordinary admission and use a validated local return URL.

Introduce an Identity-owned authentication-admission port. The standalone host
supplies a startup policy requiring a global Platform administrator after
administration becomes available and until readiness. Apply it after credential
validation, before local or BFF session issuance, during BFF session validation,
and before enabled OIDC tickets are signed in. Normal account/credential checks
remain authoritative. A post-authentication gate also denies retained ordinary
sessions and delegated API calls during startup. Health, verified enrollment,
trusted BFF transport, authentication/logout and static assets retain their own
existing protocol boundaries; allowing a path does not grant application access.
This narrows ADR-0242's pending/failed administrative access rule.

Project the existing startup coordinator through the Bootstrap contracts. The
current implementation has four actual stages: topology, required extension
contributions, resource/dependency application and finalization. Model waits are
activities within resource application, rather than a fabricated independent
pass over every provider. Report real resource outcomes and typed dependency
identities. Do not parse logs, expose raw exception messages or infer readiness
from provider health.

The first delivery uses synchronized process-local immutable snapshots, with a
new attempt identity at Begin. Reconnection observes the current attempt; restart
starts a new observation and retains existing create-only Bootstrap recovery.
It introduces no durable progress journal, history migration or distributed
observer protocol. Existing persisted initialization leases remain authoritative
for execution. Durable manual application/progress remains #871. The finer
startup-history and critical-worker barriers from the broader #1250 plan can be
delivered separately; this change preserves existing readiness semantics.

An authenticated admission endpoint exposes only readiness. Full progress requires
PlatformAdmin. Both are non-cacheable and read local observations without provider
calls or default-resource provisioning. The shared Console client supports both
embedded and independently hosted Console navigation. Active progress polls at
two-second intervals, retains the last observation on transient errors, and
discloses no raw transport diagnostics. Panels are provider-neutral.

An extension/model arriving before the configured deadline lets the coordinator
continue. After a terminal failure, keep the sanitized progress screen and
correction/restart guidance. UI refresh never re-executes Bootstrap, downloads a
model or changes an operator's resource decisions.

## Consequences

- No new startup activation switch, deployment topology or database migration.
- Pending startup no longer opens the partial overview or admits ordinary users.
- UI and application/API tests cover privacy, admission, progress and redirection;
  existing authentication behavior must still work after readiness.
- The separately hosted Console relies on its authority for admission and progress;
  its own process health is not authority readiness.
- Browser smoke remains required now. The new reusable journey #1251 stays deferred
  until explicit maintainer UX validation and implementation merge.
