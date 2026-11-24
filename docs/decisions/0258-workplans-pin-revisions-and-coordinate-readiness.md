# ADR-0258: WorkPlans pin revisions and coordinate readiness

## Status

Proposed — implementation for [#948](https://github.com/gbaudrit/agentstration/issues/948).

## Context

Functional task hierarchies describe decomposition and acceptance. They do not express that one independent task must wait for another. Work needs a durable intent and dependency graph without becoming another Flow designer or copying Resource Planning's reviewed resource proposals. The future shared Project adapter also needs a stable plan identity and an exact revision without importing local Workspace or Principal identities.

## Decision

- Own WorkPlans in the existing Work module and Work store. Use a caller-supplied `WorkPlanId`, an immutable intent/membership/dependency revision, a mutable versioned lifecycle head, and immutable command receipts. Derive Workspace and Principal ownership from trusted context, following ADR-0135. No ownership override is exposed.
- Store heads, immutable revisions and receipts in `WorkPlans`, `WorkPlanRevisions` and `WorkPlanCommands`. Revisions and receipts reference the scoped plan head. All reads include Workspace and Principal. SQLite creates missing tables during initialization; PostgreSQL uses the generated migration.
- Pin each revision to the canonical task IDs and their immutable creation-intent digests. Compute the revision content digest from the normalized intent, sorted task references and sorted dependency edges. Exclude Workspace, Principal, timestamps, mutable task state and command identifiers. `(PlanId, revision number, digest)` identifies the exact portable intent. A later adapter owns transport and any mapping of identities; it must not copy local database records as a sharing format.
- Keep the explicit dependency graph separate from functional hierarchy and Flow composition. Dependency endpoints must be plan members, and the explicit graph must be acyclic. A selected composite must include all its direct required children; their completion constraints are checked together with explicit edges to reject indirect deadlocks. Optional children may remain outside the plan. These derived constraints are not inserted into the stored explicit dependency list.
- Create Draft plans; validate the exact revision before activation. Activation pins both revision number and digest. Active plans cannot be revised. Deactivation is explicit and requires inactive member execution. Revising Draft, Validated or Inactive plans creates another immutable revision and clears validation. A new revision checks both retained and removed members for active execution.
- Define active execution as Queued, Running, WaitingForInput, WaitingForApproval or Paused. Draft creation and validation may observe existing active work, but activation, revision and deactivation cannot cross active work. Lock and compare all observed task versions in the same store transaction as the plan head, revision and command receipt. Conditional no-op updates acquire row locks without changing task versions, history or payload. This prevents a task starting between application preflight and plan commit.
- Require a stable command UUID and expected plan version. Lifecycle commands additionally identify the exact revision. Store the semantic input digest, applied plan version, revision reference, server time and observed task versions. Replay returns current state without another write; another intent under the same command identity is a conflict. An execution-strategy retry after an uncertain commit resolves the same receipt.
- Expose live readiness separately from activation: Accepted tasks are already satisfied; pending atomic tasks become Ready only in an Active plan when every explicit prerequisite has functional acceptance and local execution is Pending. Composite outcomes remain derived. Runtime completion alone does not satisfy dependencies. Reasons and unmet prerequisite IDs are technical protocol values. Readiness is advisory; #949 must perform atomic execution admission rather than trusting an earlier read.
- Bound plans to 100 tasks, 1,000 explicit edges, 8,000 intent characters, 100 revisions and 256 commands. Bound definition requests to 256 KiB and lifecycle requests to 4 KiB. Existing receipts remain replayable after history limits are reached.

## Consequences

The generic path remains executable with SQLite, without Git, an external issue tracker, a model or a Runtime Worker. It creates no Flow or Runtime Run and performs no automatic dispatch. WorkPlan intent belongs to Work; FlowDefinition and ResourcePlan retain their existing ownership.

The PostgreSQL migration creates only the three WorkPlan tables and their constraints/index. Regenerating the snapshot also records the already configured `EntryDraft.Payload` concurrency token; this adds no alteration of existing tables. Live PostgreSQL execution remains opt-in; model/snapshot alignment is checked offline.

Repository issue association, the one-issue/one-plan rule in issue-realization mode, issue acknowledgement, issue hierarchy and inter-issue dependencies remain consumers owned by #1346. Project sharing remains #1345 and execution admission/dispatch remains #949. No new browser journey or template propagation is introduced.
