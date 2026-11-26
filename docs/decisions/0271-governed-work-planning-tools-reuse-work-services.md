# ADR-0271: Governed Work planning Tools reuse Work services

## Status

Proposed — implementation for [#954](https://github.com/gbaudrit/agentstration/issues/954).

## Context

An agent needs the same local functional tasks and durable WorkPlans as REST callers,
without tracker credentials, templates or arbitrary Management write authority.
Existing internal MCP Tool projection, assignment, execution hooks and audit already
supply the provider-neutral execution boundary.

## Decision

- Add twelve internal Tools for bounded Project lookup, task-tree creation/read,
  explicit functional outcomes, and WorkPlan get/propose/revise/validate/activate/start/advance.
  Delegate every operation to the existing Work application services. Use a shared
  WorkPlan transport mapper for REST and MCP. No additional store or scheduler is introduced.
- Require exact trusted Tenant, Workspace and Principal coordinates. Reject scope,
  Principal, command identity and causal fields from model arguments. Agent calls must
  carry an explicit Workspace Tool assignment from the trusted execution material.
  Recheck the current RunsRead or RunsExecute permission before accessing Work.
  The AWP participant operation gate continues to validate the assigned Agent revision.
- Derive a stable command UUID from trusted Tenant, Workspace, Principal, Tool name and
  Tool call ID. The canonical resource-scoped receipt checks semantic intent and replay;
  callers retain the same plan/task identity on retry. The existing Tool lifecycle audit
  retains the call, run, participant and correlation coordinates. No model-provided
  causation metadata acquires authority.
- Preserve the Tool pipeline's enablement, availability, input validation, managed deny
  hooks and audit. Raw MCP calls cannot invoke an internal Tool configured to require
  approval; approved Agent calls retain the existing runtime approval wrapper. No new
  approval protocol is added. Normalize schema validation failures into bounded MCP
  errors using the existing technical error codes.
- Bound Tool arguments to 512 KiB, call IDs to 256 characters, Project pages to 100 and
  task trees/plans to their existing domain limits. Reject undeclared properties at
  the outer boundary and while deserializing nested commands. Dry runs validate the
  declared structure without effects; lifecycle, referenced resources and execution
  authority are checked by canonical services during execution.
- Keep completion and functional acceptance separate. The explicit outcome Tool
  records evidence through the same concurrency and consolidation rules as REST.
  Start configures the existing durable scheduler; advance remains safe to repeat.

## Consequences

Local planning needs no TaskProvider, TaskTemplate or remote service. Tool assignment
and approval remain independent of Project/resource ownership. APIs, MCP and workers
observe the same persisted plans and immutable revisions. A replay after restart
cannot create another attempt for the same admitted task through this surface.

There is no new browser journey or reusable browser scenario. External task operations
remain #955, GitHub operations #958 and shared issue coordination #1346. These Tools
neither publish external tasks nor grant arbitrary resource mutation authority.
