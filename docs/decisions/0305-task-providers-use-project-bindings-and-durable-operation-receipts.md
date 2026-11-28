# ADR-0305: Task providers use Project bindings and durable operation receipts

## Status

Proposed — implementation for [#955](https://github.com/gbaudrit/agentstration/issues/955).

## Context

Local tasks and plans do not need a tracker. Optional external issue reads, updates
and parent publication need an explicit Project connection and recoverable writes.
A response can be lost after the remote effect or before the local receipt is saved.
Choosing a different provider or issuing another publication on retry is unsafe.

## Decision

- Append `TaskOperations` to Project binding purposes. Bind an exact, authorized
  ToolProvider identity and select that binding by name. Do not introduce a mandatory
  TaskProvider Management resource or another AEP enrollment mechanism.
- Discover operation capabilities from existing AEP Tool contribution metadata,
  persisted by the normal Tool discovery path. The `agentstration.work.task-provider`
  contract version `1.0` declares supported Read/Update/Publish operations, operation
  idempotency and recovery. Exactly one enabled, available declared Tool must support the operation;
  missing, ambiguous, disabled, unavailable or unapproved routes fail closed.
- Keep the application port provider-neutral. Resolve current Discover/Use authority,
  pin provider and Tool UID/generation, and invoke the normal governed Tool pipeline.
  Carry exact trusted Tenant, Workspace and Principal coordinates. Reauthorize the
  pinned route before each effect; never silently move a pending operation to a new
  provider or generation. Bound calls to 30 seconds and structured responses to
  256 KiB (512 KiB for the enclosing MCP result). Use the existing protected AEP/MCP
  credentials; requests contain no credential configuration.
- Persist `WorkTaskOperations` in the Work store. Atomically guard the observed
  Project and optional WorkPlan version when admitting an operation. Capture the
  selected binding, exact plan revision/digest, normalized input, intent digest and
  opaque provider operation key before dispatch. Keep these receipts separate from
  Project configuration versions and from plan activation. Supply SQLite upgrade
  initialization and an equivalent PostgreSQL migration/model.
- Scope every operation query and update by Workspace and Principal. Use optimistic
  versions and immutable receipt coordinates. Pending/Uncertain operations may become
  Succeeded or Rejected; terminal receipts cannot be overwritten. Page history by
  immutable creation time and ID, with an explicit bounded `hasMore` contract.
- Require provider-side idempotency for the opaque operation key and recovery of its
  result. This is a provider obligation, not a claim that arbitrary tracker APIs are
  idempotent. After response loss, cancellation, restart or a local completion-write
  failure, explicit replay first requests the original receipt. Only an authoritative
  Missing result may resubmit the same key, under the same still-authorized route and
  current plan intent. Providers that cannot prove absence retain Uncertain and must
  not emit Missing. There is no automatic retry worker or AWP worker recovery.
- Require an observed external revision for updates. Providers enforce that precondition
  and return a new revision. Keep issue read/update/publication separate from local
  Work lifecycle. Child tasks remain internal unless explicitly published by a consumer;
  one-issue/one-plan orchestration and native repository relations remain #1346.
- Reuse the existing security audit writer through a Tool hook for Work planning and
  external operations, including failed calls. Store only trusted actor/scope and
  bounded technical call coordinates; retain detailed Tool governance in the existing
  Runtime/Flow audit for run-owned calls. Never audit request bodies or external content.

## Consequences

The offline SQLite product remains executable with no provider installed. AEP/MCP
providers can expose a subset of operations, including no publication. Deterministic
application tests inject failures around receipt persistence; an offline AEP/MCP
fixture exercises real discovery, routed calls, restart recovery and optimistic updates.
No live vendor behavior is inferred from those fixtures.

GitHub labels/relations and concrete repository permissions belong to #958; GitLab
remains outside the minimum path. Sharing, Coding execution, Workspace seeding and
usage aggregation consume these receipts later. No browser journey is introduced.
