# ADR-0240: Initial Bootstrap waits for required extension contributions

## Status

Accepted

## Context

An extension cannot announce while the authority waits before listening. Complete
state-dependent preview also cannot precede creation of topology and contribution
resources. A global extension list or fixed startup delay cannot describe the
requirements of arbitrary selected Bootstrap resources.

## Decision

`Agentstration:Bootstrap:PhasedStartupEnabled` explicitly enables staged startup.
The existing pre-listener path remains the default. Storage initialization still
finishes first. The listener then accepts health requests and proof-protected AEP
announcements while other requests return a not-ready Problem response.

Initial startup structurally prepares bounded local catalog applications, applies
topology, applies instance enrollment policies, and activates automatic assignment.
`InitialProfiles` retain their instance target. Additional `InitialApplications`
declare ordered profile sets and explicit `TargetLevel` selectors resolved after
topology creation. Each application retains homogeneous profile scope. Scoped
applications execute through an existing Platform administrator identity and the
normal Tenant/Workspace context and authorization boundaries.

Selected public selectors and consumed contribution bindings determine the
blocking set. Existing family provisioning is responsible for materialization;
registration alone does not satisfy a contribution dependency. Only required
extension identities are processed in the startup wait, so an unrelated optional
extension cannot consume the deadline. Waiting is cancellable and bounded by
`ExtensionDependencyTimeout` (ten minutes by default, one second to thirty minutes).
The default accommodates recovery of an interrupted automatic enrollment lease.
Disabled contributions and ambiguous selectors fail explicitly. Bootstrap never
silently re-enables an operator-disabled Tool. Existing extension-declared bundle
requirements retain their normal trusted provisioning behavior.

Catalog digests are rechecked after waiting. Remaining resources undergo normal
preview/resolution and create-only application in deterministic application order.
Readiness becomes true only after the full sequence. Application workers are
deferred as an ordered group, including Quartz schema/scheduler ordering; health,
transport and telemetry can start earlier. Startup System context must not flow
into listener or worker execution contexts.

Staged startup reacquires the existing fenced initialization lease even when the
durable initialization version is already Ready. Resource handlers, enrollment
claims and contribution installation remain idempotent. Failures retain the
existing durable initialization diagnostic, and a retry preserves prior resources.
Late optional extensions use the same policy worker after readiness.

## Consequences

- An externally started extension may arrive after topology creation without
  causing premature reference validation or a listener/enrollment deadlock.
- The old manual preview remains read-only; no new Console workflow is introduced.
- Existing NoBootstrap and non-staged startup retain their documented behavior.
- This is startup orchestration, not a general resource reconciler or process
  supervisor. Extension installation and startup remain external.
