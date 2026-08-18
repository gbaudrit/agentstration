# ADR-0055: Agentstration owns the Tool execution boundary

## Status

Accepted

## Context

The governed Tool catalog resolves canonical `ToolResource` assignments to MCP Tools, including AEP contributions that map to MCP. The MAF adapter previously preferred a native `AITool` returned through `IAgentTool.GetService(typeof(AITool))`. For MCP this exposed `McpClientTool`, or an `ApprovalRequiredAIFunction` around it, directly to MAF. Effective invocation could therefore bypass `IAgentTool.InvokeAsync` and any future Agentstration-wide audit or policy boundary.

Runtime and Flow execution are durable and at-least-once. Tool execution must preserve the current Tenant, Workspace and Principal scope, distinguish the logical external effect from a physical invocation attempt, and retain the existing durable MAF approval workflow. AEP must remain an identity and contribution layer over MCP rather than becoming another Tool invocation protocol.

## Decision

Agentstration owns a mandatory, provider-neutral Tool execution boundary:

```text
Agent / MAF
  -> provider-neutral Tool descriptor
  -> Agentstration IToolExecutionPipeline
  -> provider-specific IToolInvoker
  -> MCP tools/call
```

`IToolCatalog` continues to resolve available Tools for an Agent. Its `IAgentTool` values are descriptors containing canonical, provider and external identities, schemas, descriptions and `RequiresApproval`; they expose neither `InvokeAsync` nor a service lookup for native provider Tools.

The MAF adapter always constructs its own `AIFunction` from that descriptor. Invocation constructs a `ToolExecutionContext` and calls `IToolExecutionPipeline.ExecuteAsync`. The default pipeline delegates to the configured `IToolInvoker`; the MCP implementation revalidates current Tool and provider governance before issuing `tools/call`. AEP contributions resolve to the same MCP invoker.

The pipeline publishes provider-neutral lifecycle facts to `IToolExecutionEventSink` immediately before provider invocation and after success, failure or cancellation. This sink is an observability/projection port, not a configurable execution hook: it cannot mutate arguments, replace results or authorize an invocation. A failure to persist the started fact prevents the external invocation. A terminal projection failure after a successful provider effect is surfaced to the execution owner and may therefore lead to an at-least-once replay. A terminal projection failure never replaces the original provider exception.

Runtime Runs project those facts into one `RuntimeToolCall` per logical `ToolCallId`, with the latest physical `InvocationId`, an attempt count, state and duration. Flow Runs append corresponding `ToolCallStarted`, `ToolCallCompleted` and `ToolCallFailed` events to their own durable event journal. Arguments and provider results are deliberately not included in either durable projection by default; failure diagnostics retain the provider exception type and message.

`ToolExecutionContext.ToolCallId` represents a logical Tool Call and `InvocationId` represents one physical attempt. The MAF adapter uses a provider call identity when the invocation API surfaces one; otherwise it derives a deterministic fallback from the durable execution identity, agent revision, Tool identity and arguments so replay/resume does not arbitrarily create a new logical effect identity. This is correlation and future idempotency-policy groundwork, not an exactly-once guarantee. Provider idempotency and automatic retries remain out of scope.

For a Tool with `RequiresApproval`, Agentstration wraps its own `AIFunction` in MAF `ApprovalRequiredAIFunction`. The existing `RequestInfoEvent` → durable `InputRequest` → `WaitingForInput` → checkpoint resume mechanism remains unchanged. Approval authorizes the call but does not bypass the Tool execution pipeline.

This decision supersedes only the native-invocation portions of ADR-0027 and ADR-0028. MCP remains responsible for protocol negotiation, schemas, `tools/list`, `tools/call` and results. MAF remains responsible for model-driven Tool selection and the current durable approval interaction, but is not the owner of effective Tool invocation.

## Consequences

- Direct Agent Runtime Runs and MAF Flow orchestrations use the same mandatory execution boundary.
- Workspace and available execution identity data reach the boundary without introducing MAF or MCP types into Runtime contracts.
- Native MCP Tools may supply discovery metadata but cannot be handed to MAF as an invocation capability.
- Provider cancellation and diagnostics cross the boundary unchanged.
- `RuntimeToolCall` and Flow Run events expose started, completed, failed and cancelled Tool attempts without storing arguments or results by default.
- Replays keep one logical Runtime Tool Call while incrementing its physical attempt count; this remains an at-least-once record, not an idempotency guarantee.
- The local ordered Runtime hook chain is specified by ADR-0056. Configurable Hook resources, DLP/PII, quotas, redaction, generic policies, input/output mutation, automatic retries and exactly-once effects remain future work.
