# ADR-0253: Initial Bootstrap admits its own Worker assignments

## Status

Accepted — 2026-11-23. Owning feature: [#1279](https://github.com/gbaudrit/agentstration/issues/1279).

## Context

Discovery-bearing catalogue profiles need their published built-in or extension
Flows during initial Bootstrap. ADR-0250 defers application background services
until readiness, and the startup HTTP fence also prevents AWP communication.
Waiting for readiness before starting a Worker creates a circular dependency.
An existing instance can also contain ordinary pending assignments that must
remain suspended while initial applications are replayed.

## Decision

Expose six ordered preparation steps: topology, Worker communications, built-in
resources, extension contributions, profile resources and finalization. Opening
Worker communication does not require a registered Worker and does not declare
the instance ready. Existing AWP enrollment, authentication, session, assignment
and governed-operation endpoints retain their protocol authorization checks.
Aspire Workers wait for authority process startup rather than readiness.

During the structure phase, Workspace provisioning activates the structural
identity needed by default-context bindings and defers platform resources to the
built-in phase. Global readiness still prevents ordinary use of those Workspaces.
Workspace creation after readiness retains its normal complete provisioning path.

Built-in publication uses the local Schema format handlers during structure,
communication, built-in and finalization phases. The AEP format-source adapter is
admitted for profile/contribution processing and normal ready operation. This
prevents core JSON Schema validation from discovering unavailable optional AEP
providers while preparing the platform. Registry pinning and ambiguity semantics
remain unchanged. Configured-extension discovery runs in the extension phase,
after built-in publication and before profile resources; required contributions
retain their existing enrollment path. Administration opens after enrollment
policies have been validated and applied.

Initial discovery uses the normal discovery service, durable root Flow assignment
and AWP execution path under an explicitly authorized Platform administrator and
the existing logical/operational scope rules. The root Flow queue creates its
assignment synchronously; nested Flows execute under that same assignment.

A narrow Runtime-owned assignment-admission port supplies an optional set of
server-approved assignment identities to atomic Worker claims. Null admits all;
an empty set admits none. During initialization only the current Bootstrap
discovery assignments are admitted. Grants are attempt-bound, remain valid through
assignment closure, and are cleared on completion, failure or retry. Flow success
can precede the Worker's terminal assignment report. Owned-operation authorization
also fences old leases. SQLite and PostgreSQL apply the same atomic claim filter.
No assignment schema, Worker wire contract or client-supplied bypass is added.

This refines ADR-0250's execution fence: authenticated AWP transport is admitted
early, while ordinary queued work, recovery workers and Quartz scheduling remain
behind final readiness. Discovery requires a compatible Worker only at the
instruction that needs it. A declarative profile has no Worker prerequisite.

## Consequences

- Initial and manual applications share discovery, immutable pins and governed
  Tool invocation. Built-ins and contributions are ready before profile execution.
- Startup progress reports Worker waits, capability, deadline, elapsed stage time
  and the authorized discovery Flow Run link. Timeout/cancellation retain completed
  prerequisites and prevent downstream creation.
- Create-only retry reuses discovered operations and existing durable evidence;
  retrying does not release ordinary assignments or weaken scope isolation.
- A new explicit application chooses the first instruction attempt key without a
  prior terminal failure. It resumes unfinished discovery and reuses success;
  failure in the current application stops dependent creation without auto-retry.
- Platform administrators remain able to inspect failed startup. Full readiness
  and ordinary execution require successful finalization.
