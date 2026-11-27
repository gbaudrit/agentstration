# ADR-0293: Sharing carries connection needs without local assignments

- Status: Proposed — implementation for #1345
- Date: 2026-11-27
- Owners: Work Plane
- Related: #1345, #951, #958, ADR-0275, ADR-0284, ADR-0291, ADR-0292

## Context

Independent instances share functional work but have different ConnectedService, type,
Tool and ExtensionRegistration identities, scopes and credentials. Sharing a local execution
selection would either lose the declared connection need or replay another instance's
configuration. A matching contract alone cannot establish the same external authority and target.

## Decision

The existing strict YAML sharing format carries one canonical `ProjectMetadata/project`
record. Its `connectionRequirements` contains portable named contract/version/operation needs
and optional external provider/target identities from ADR-0291. No local assignment, physical
resource reference, credential or resolved route is a field of this contract. Unknown typed
fields are rejected. Requirements and operations normalize by ordinal name before hashing.
Publishing this record requires its requirements to match the authorized local Project.

The functional WorkPlan definition adds `taskConnections`: atomic member task identities
and the logical names they consume. Names participate in immutable revision and command
identity, survive draft import and are available through the existing REST/MCP contracts.
They contain no Flow, Input, execution attempt or local connection identity. Empty selections
remain provider-optional. Shared consumed names must exist in the canonical metadata and
must pin an external provider/target identity. A consumed requirement without a target fails
explicitly rather than allowing two participants to silently use different external systems.
Snapshot reads and CAS merges enforce these cross-record references; removing a declaration
still consumed by another plan is a conflict. Multiple needs may use the same contract.

Pull remains observation only. `POST sharing/connections/import` explicitly applies the
current metadata requirements using the observed metadata digest and local Project version.
It reuses Project validation and atomic optimistic persistence, replacing only requirements.
Assignments, ordinary bindings, sharing configuration, membership and execution remain local.
Removing a locally assigned need or making an assignment incompatible is rejected; no implicit
service selection, scope recreation or assignment cleanup occurs. A deterministic receipt
identifies the exact digest/version application. A retry acknowledges a completed application
without overwriting later local edits, including after restart.

Execution uses declared task names when local target selections are omitted; an explicit
different selection is rejected. Shared admission checks that the selected names and each
consumed local requirement still match the shared declarations. It retains metadata revision
and digest evidence alongside plan/allocation evidence. Existing ADR-0291 resolution checks
the selected local service and actual target-specific ATC/AFC Describe proof. Independent
service/type/extension UIDs and granted scopes may differ; external authority and target may
not. Unrelated unconfigured needs do not block the task. Previously admitted attempts and
uncertain local operation receipts are never imported or rewritten by synchronization.

This extends the unreleased format 2.0 and existing JSON persistence payloads, without a new
store, provider SDK, scheduler or transport. Fresh/reseeded instances and tracking fixtures
use the complete contract together. No compatibility read, database migration or automatic
conversion of old digest-addressed documents is supplied.

## Consequences

- Portable configuration can be reviewed and versioned with the existing YAML work data.
- Each installation explicitly assigns its own compatible services, without matching a local UID.
- Declaration changes can deliberately block new admission until explicitly reconciled locally.
- Shared publication/recovery retains the existing CAS, history, bounds and local authority.
- Git, ATC and AFC remain separate capabilities; ordinary offline work needs none of them.
- Native Coding issue/delivery adoption remains owned by #1346/#1349.
