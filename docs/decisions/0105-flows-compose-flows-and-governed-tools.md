# ADR-0105 — Flows compose reusable Flows and governed Tools

## Status

Accepted

## Context

Agentstration already has one durable Flow runtime, immutable published Flow versions, and a governed Tool execution pipeline. Reusable orchestration nevertheless cannot be expressed in the typed graph: authors must duplicate a graph, delegate deterministic effects to an Agent, or introduce application-specific execution code. Provider-specific cards and notification-specific nodes would expand the engine vocabulary whenever a resource or integration is added.

The same business capability must also be invocable from Entries, Triggers, APIs, MCP clients, authorized Agents, the Console, and another Flow without granting a caller unrestricted access to every Flow.

## Decision

- The typed graph gains exactly two generic composition primitives: a Flow call step and a governed Tool step. Resource catalogs populate these cards; a resource never creates a new node type.
- A Flow call selects a logical Flow in the current Workspace and resolves either its active published version or an explicitly selected immutable version. Its input is an explicit schema mapping and its declared output schema becomes available to subsequent steps.
- Publication resolves the referenced Flow in the caller's Workspace and namespace, validates its published contract, and rejects direct or indirect dependency cycles. Dynamic Flow names are not accepted.
- Runtime execution creates a durable child FlowRun inside the existing WorkItem. The child captures the exact resolved version and causality; the parent releases its worker while waiting and resumes from persisted state. Recursion and descendant counts are bounded.
- A Tool step selects an ordinary governed Tool resource and executes through the existing Tool execution pipeline, including enablement, approval, hooks, audit, and provider invocation.
- Root Flow submission converges on one application boundary. Entry, Trigger, REST, Console, internal MCP, and authorized Agent adapters provide trusted caller context outside model-controlled input. Nested Flow calls use a separate child-run boundary.
- Workspace-scoped ToolDefinition resources may publish individually bounded, Flow-backed Tools through Agentstration's internal MCP server. They do not expose generic Flow-start or Management CRUD authority.
- Delivery channels, including notification delivery, are ordinary reusable Flows whose terminal effects are governed Tools. There is no NotificationChannel resource or notification-specific engine node.
- Packs distribute the same generic graph definitions and retain their Flow and Tool dependencies. Recipes may create ordinary editable graphs but do not extend runtime semantics.

## Consequences

The palette stays finite while installed Flows and Tools remain dynamically selectable. Active references allow a reusable implementation to change without editing its callers; every execution still records the exact effective version. Exact references provide reproducibility where callers require it.

Flow Application remains provider-neutral and owns composition validation and durable orchestration. MCP and provider details stay behind the existing Tool boundary. Workspace authorization, cancellation, restart recovery, idempotency, and at-least-once external-effect semantics must be explicit in the executable increments.

This decision supersedes the deferred subflow limitation in ADR-0019 without changing its ownership of FlowRun state. It does not revive CompositeFlowDefinition as a parallel authoring model and does not introduce a second automation runtime, an external broker, arbitrary code handlers, direct HTTP steps, or exactly-once external effects.
