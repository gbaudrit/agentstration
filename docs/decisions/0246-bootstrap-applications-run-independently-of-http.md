# ADR-0246: Bootstrap applications run independently of HTTP requests

Status: Accepted — 2026-11-18

Renumbered from ADR-0238 when rebasing on the phased Bootstrap branch, which
retains main’s fragment-navigation ADR-0238. The decision is unchanged.

## Context

Issue #871 demonstrates that the synchronous manual application in ADR-0077 can
outlive the Console's ordinary HTTP request budget. Disconnecting after resources
have been created leaves the operator without an authoritative result. Increasing
that budget does not provide durable progress or safe reconnect behavior.

## Decision

The administrative application endpoint persists a `Running` BootstrapApplication
and returns `202 Accepted` with its stable read Location before preview validation
and resource execution. A bounded admission path permits at most 16 pending or
executing manual applications. A hosted processor executes accepted applications
sequentially in fresh dependency-injection scopes, independently of HTTP request
cancellation. It delegates all planning, binding, validation and resource mutation
to the existing Bootstrap application service and resource handlers.

The initiating request carries an optional GUID request identity. Reusing that
identity with the same actor, selection and expected digest returns the original
application, including after completion; different input is rejected. An identical
active selection from the same actor also returns the active application. The
Console keeps one request identity for a preview, suppresses concurrent confirmation,
and never retries application automatically while observing progress.

Execution revalidates the actor, explicit Tenant/Workspace target and expected
catalog digest before the first mutation. Validation that previously happened in
the application POST may therefore appear as a failed accepted application. Each
resource boundary persists total resources, current safe resource identity and
completed outcomes. Progress contains no manifest definitions or resolved values.
Handler failures retain a safe diagnostic code instead of arbitrary exception text.

The Console polls the stable read endpoint once per second, derives outcome counts
from persisted results and reloads active history after refresh. A transient read
failure keeps the existing identity and reconnects. Cancellation records a durable
request and is cooperative between resources; an already-running resource may
finish. Cancellation, failure, partial application and interruption remain distinct
terminal history states, preserving completed outcomes.

Each application records its process owner. Live reads never wait for execution
and do not classify the current process's active applications as interrupted. On
restart, applications belonging to the former process become interrupted when the
processor or history reads them. They are never replayed automatically because a
resource side effect may have preceded its outcome write. This retains the
single-active-authoritative-process assumption of ADR-0077; distributed execution
would require a fenced ownership protocol rather than process-owner comparison.

## Consequences

- The ordinary Console timeout remains unchanged; resource execution no longer
  consumes the initiating request budget.
- API consumers must follow the `202` Location to obtain the terminal outcome.
- Existing JSON resource persistence stores additive progress fields; no relational
  migration or alternative Bootstrap execution store is introduced.
- Graceful shutdown records interruption. Abrupt termination preserves the latest
  durable resource boundary and is recovered without duplicate execution.
- Bootstrap remains create-only, sequential and best effort, with no rollback.
- The versioned, test-only `load-test` catalog profile contains 512 offline Tenant
  Parameters and is never included in standard initial bootstrap selections.
