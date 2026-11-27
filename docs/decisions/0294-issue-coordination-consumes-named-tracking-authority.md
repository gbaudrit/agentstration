# ADR-0294: Issue coordination consumes named tracking authority

- Status: Proposed — implementation for #1346
- Date: 2026-11-27
- Owners: Work Plane and Coding
- Related: #1346, #951, #958, ADR-0276, ADR-0290, ADR-0291, ADR-0293

## Context

The executable issue workflow still selects one native TaskOperations binding and interprets
GitHub labels in Coding. Named requirements and service-backed ATC now provide the same bounded
read/update and recovery path without coupling shared work to local registrations or accounts.
Two independent participants need one native issue/plan association while keeping private proof.

## Decision

Issue intake explicitly selects a named Tracking requirement with read/update operations and an
expected external provider/target. It resolves the locally assigned ConnectedService through the
existing ATC Work gateway and governed protected transport. No implicit native fallback or first
service selection remains. Canonical Project metadata must contain the same approved requirement.

Shared issue references add the logical connection name and provider-qualified tracking target.
The target/item tuple establishes unique native identity; repository names remain checked display
and policy coordinates. Local service/type/registration/Tool UIDs, accounts, protected inputs and
recovery routes never enter this portable reference. Strict snapshot and CAS validation bind its
name and target to shared metadata and reject duplicate associations despite repository renames.
This is an unreleased contract change with reseeded fixtures, not a compatibility migration.

The existing issue binding policy becomes the narrow provider adapter for authority observations
and acknowledgment input. Its first implementation maps GitHub labels, native completion reasons
and service repository/managed-prefix policy in Infrastructure. Coding consumes plan identity,
participant acknowledgment, open/completed predicates and native dependency references. ATC's
generic classifications and relation contracts do not acquire an Agentstration plan lifecycle.
Unsupported providers or guarantees fail explicitly; no feature is fabricated from labels.

Each root and upstream observation uses the canonical Work operation service. A verified route
digest is an optional admission precondition on that existing service; it is part of command
identity and checked before preparation/persistence/provider invocation. Existing parameter,
service and protected-header guards remain immediately before invocation. This closes the gap
between policy resolution and operation admission without introducing another receipt engine.

Readiness verifies the locally confirmed acknowledgment against the current complete route,
refreshes complete native dependency observations and resolves each upstream through its own
explicit connection. Unknown, incomplete, unavailable, abandoned or conflicting authority blocks
new admission. The existing bounded gate retains native and opaque connection-digest evidence.
An assignment change requires a new explicit acknowledgment command, preserving old operation
receipts. An uncertain intent never switches account or target. Active attempts keep their own
captured evidence; native relationship changes neither cancel nor duplicate them.

Existing SQLite/PostgreSQL JSON receipts and the private confirmed-operation mapping remain.
There is no new relational model, scheduler, provider SDK or generic Project requirement.
Trackerless work remains offline; logical epics do not add native relationships or gates.

## Consequences

Two instances can map the same portable need to different accounts, service UIDs and scopes
while proving the same external target. Issue intake and dependency checks run without an agent
or LLM. GitHub policy stays in its adapter; AFC delivery adoption remains #1349, seed assignment
review remains #1347, and integrated UX retains its owning issues and validation gates.
