# ADR-0105 — Notification channels are delivery Flows

## Status

Accepted

## Context

Workflows need to deliver messages through the in-product notification center or external MCP providers without coupling every parent graph to a provider schema. MCP does not define a universal notification contract, so Agentstration cannot infer equivalent Slack, Teams, email, and vendor-specific Tools.

## Decision

- A notification channel is an ordinary reusable Flow with an explicit stable input/output contract. It is called from parent graphs through the generic Flow step.
- Agentstration publishes the bounded atomic internal MCP Tool `work.notification.create`. It creates a durable Workspace notification through `WorkplaceService`; Tenant, Workspace, Principal, run, step, Tool-call, and correlation context remain trusted runtime data rather than Tool arguments.
- An explicit delivery key deterministically identifies a notification inside a Workspace. Replaying the same logical delivery returns the existing notification without emitting duplicate creation events.
- Internal MCP Tools are described through the same MCP schema abstraction and projected as ordinary Tool resources under the reserved `agentstration` provider. Flow and Agent invocation therefore reuse Tool validation, governance, execution hooks, and audit.
- A `ToolDefinition` such as `notification.send` may expose the delivery Flow. The delivery Flow terminates on the atomic Tool, not on its own Flow-backed ToolDefinition, preventing recursion.
- Replacing the channel means publishing and activating a contract-compatible delivery Flow version that maps to another MCP Tool. Parent Flows and active-reference ToolDefinitions remain unchanged.

## Consequences

There is no `NotificationChannel` resource, notification engine step, provider-name inference, or external credential in the local default. Fan-out, conditions, transforms, fallback, and failure behavior use ordinary graph primitives. Explicit Workflow delivery remains distinct from event-driven observables in #251, which may call the same Flow after deciding that an event is actionable.

The notification record retains delivery and causal identifiers but no complete Tool arguments or provider payload. Its deterministic identifier uses the Workspace and delivery key, so persistence requires no relational migration.
