# Governed Resource Planning Tools

Resource Planning publishes seven internal MCP Tools through the standard Agentstration Tool projection and execution pipeline:

- `resource-planning.plan.create`
- `resource-planning.plan.get`
- `resource-planning.plan.refine`
- `resource-planning.plan.submit`
- `resource-planning.plan.materialize`
- `resource-planning.change-set.create`
- `resource-planning.change-set.validate`

Their schemas contain functional solution, role, workflow, integration, experience, dependency, and runtime intent only. They do not accept Tenant, Workspace, Principal, canonical Resource envelopes, or an apply/delete operation.

Tenant, Workspace, Principal, correlation, run, step, caller, and causation values come from the trusted Tool execution context. Normal Tool assignment, approval, receipt, audit, and replay behavior therefore applies. Materialization and validation call the same Resource Planning application services as the HTTP API and never apply a proposed change set.
