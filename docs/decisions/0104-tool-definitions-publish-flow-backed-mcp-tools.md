# ADR-0104 — ToolDefinitions publish Flow-backed MCP Tools

## Status

Accepted

## Context

Agentstration already consumes external MCP Tools through governed `ToolProvider` and `Tool` resources, while ADR-0103 requires bounded business capabilities to be invocable by Agents and MCP clients without exposing a generic Flow launcher. Static attributed server methods cannot represent Workspace-authored Tools or follow an active published Flow version.

A Flow-backed Tool must preserve one public contract, capture the effective immutable Flow version on every call, participate in the ordinary Tool assignment and governance path, and keep Tenant, Workspace, and Principal scope outside model-controlled arguments.

## Decision

- `ToolDefinition` is a Workspace-owned, namespaced Management resource with ETag CRUD. It owns the MCP name, presentation, enablement, approval intent, JSON input/output schemas, bounded invocation timeout, and one exact or active published Flow reference.
- Saving or enabling a definition resolves its Flow and requires its public schemas to equal the published Flow schemas. Activating a new Flow version is rejected while it would break an enabled active-reference definition.
- Each definition materializes a normal `ToolResource` under a reserved namespaced `agentstration` MCP provider. Agents select that Tool individually and reuse the existing catalog, approval wrapper, execution hooks, lifecycle audit, and runtime adapter.
- The internal MCP server publishes only enabled definitions through dynamic `tools/list`. `tools/call` resolves the selected definition in the authenticated Workspace and invokes the shared root Flow submission boundary; it exposes no generic Flow start or Management CRUD Tool.
- A logical Tool call deterministically identifies its root WorkItem and FlowRun. Execution is cancellation-aware and bounded, returns the Flow output, and attaches the durable WorkItem, FlowRun, effective Flow version, and correlation identifiers as the MCP operation receipt.
- Tenant, Workspace, Principal, origin, causation, and correlation are derived from authenticated transport or runtime execution context. Tool arguments are only the public Flow input and cannot override execution scope.

## Consequences

Changing a notification or other capability from an internal implementation to another Flow does not change Agent assignments or workflow Tool cards. The provider remains an MCP provider from the governance perspective, but its invocation adapter is local and does not loop through an unauthenticated HTTP connection to the same process.

Control-plane storage remains document-based, so adding the resource kind requires no relational migration. A disabled definition remains editable and materialized as a disabled Tool; deletion removes its materialized Tool while the reserved provider may remain for other definitions. Arbitrary scripts, CLR handlers, direct HTTP handlers, automatic publication of every Flow, and unrestricted platform Tools remain out of scope.
