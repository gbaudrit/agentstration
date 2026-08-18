# ADR-0057: Tool execution Hook resources select built-in Runtime handlers

## Status

Accepted

## Context

ADR-0056 defines locally registered, ordered Runtime guards for Tool execution. Local dependency-injection registration is useful for embedded deployments but cannot express workspace-owned governance that administrators can persist, inspect, enable or update independently from application deployment.

A Management resource must not become an arbitrary code-loading mechanism. Resolution also occurs on every at-least-once physical attempt and must use the current durable Tenant and Workspace scope rather than a global lookup by resource name.

## Decision

Agentstration introduces the canonical `ToolExecutionHookResource` (`kind: ToolExecutionHook`). Its definition contains a display name, enabled state, order, a built-in handler identifier, selectors and handler configuration.

Selectors can constrain canonical Tool, Tool Provider and Agent names. Empty selector lists are wildcards; populated lists are combined with AND semantics. The resource itself is workspace-owned and may use a resource namespace for identity. Runtime resolution first uses the current scoped `IControlPlaneStore`, then defensively verifies the resource Tenant and Workspace against `ToolExecutionContext` before evaluating selectors.

The first supported handler is `deny`. It accepts only:

```yaml
configuration:
  code: stable_denial_code
  message: Human-readable denial diagnostic
```

Unknown handlers, configuration properties, invalid codes, duplicate selector identities and excessive selector sizes are rejected at the Management boundary. Resources cannot name assemblies, .NET types, scripts, commands or remote endpoints.

`ManagementToolExecutionHookResolver` resolves enabled matching resources on each physical Tool invocation and creates provider-neutral Runtime hooks. The pipeline combines managed hooks with locally registered hooks, then applies the stable ordering rules from ADR-0056. Missing Workspace identity means that no managed hook is resolved; locally registered hooks still apply.

The Management API exposes workspace-scoped CRUD at `/api/toolexecutionhooks`, including namespaces, ETags and Problem Details validation.

## Consequences

- Workspace administrators can persist and update a useful fail-closed Tool guard without restarting the host.
- A matching denial is projected as `Denied` and never reaches MCP, whether the Tool came directly from MCP or through AEP-to-MCP.
- Management storage or resolution failure fails the governed call before provider invocation and is classified as a hook failure.
- Every physical retry resolves resources and executes matching hooks again; no exactly-once guarantee is implied.
- DLP/PII, quotas, redaction, argument/result mutation, remote hooks, scripts, dynamic plug-ins, Pack installation support and Console UI remain future increments.
