# Tool Providers and governed tools

A `ToolProvider` is a configured source of tools. V1 supports AEP extensions and generic MCP servers over STDIO or Streamable HTTP. A provider owns connection and discovery configuration; it does not grant an agent permission to use anything.

Discovery materializes every announced capability as an `Agentstration.Tools/tools` resource. New resources are `discovered = true`, `available = true`, and `enabled = false`. Refresh updates provider-owned descriptions, metadata and MCP schemas while preserving the administrator-owned `enabled` flag. A missing tool remains persisted with `available = false`; reappearance restores availability.

```text
provider enabled
  AND tool enabled
  AND tool available
  AND canonical Tool resource assigned to Agent
  -> exposed to MAF
```

MCP remains authoritative for protocol negotiation, `tools/list`, schemas, invocation and results. AEP contributes only extension identity, display metadata and mappings to MCP. Agent definitions never contain a command, endpoint or raw MCP declaration.

Tool resolution and Tool execution are separate runtime responsibilities. The catalog returns provider-neutral descriptors (identity, schema, availability and approval metadata); it does not return an invocable MCP object. Microsoft Agent Framework receives an Agentstration-owned function adapter, and every effective invocation crosses `IToolExecutionPipeline` before the provider-specific `IToolInvoker` issues MCP `tools/call`. Direct MCP and AEP-to-MCP providers share this boundary:

```text
Agent / MAF
  -> Tool resource resolution
  -> Agentstration Tool Execution Pipeline
  -> MCP Tool invoker
  -> MCP tools/call
```

The pipeline emits provider-neutral lifecycle facts before invocation and after completion, failure, or cancellation. Runtime Runs project a logical `RuntimeToolCall`; Flow Runs append Tool Call events to their own journal. `ToolCallId` identifies the logical call across replay, while `InvocationId` and the projected attempt number distinguish physical attempts. Arguments and provider results are not durably projected by default. Lifecycle sinks are observability ports only and remain separate from execution hooks.

Locally registered `IToolExecutionHook` guards run in stable order before MCP invocation and unwind in reverse order with a terminal outcome. A hook may allow or deny a call; it cannot currently mutate arguments, inspect or replace the provider result, or request approval. Denials never reach MCP and are projected separately from provider and hook failures. Every physical at-least-once attempt runs the hooks again. Management-plane Hook resources and scoped hook selection are not implemented yet.

`requiresApproval` remains a native Tool policy. Agentstration wraps its own function adapter in MAF `ApprovalRequiredAIFunction`, preserving durable `RequestInfoEvent` → `InputRequest` → `WaitingForInput` → checkpoint resume behavior. After approval, the resumed invocation enters the Agentstration pipeline and its hook chain. The boundary carries logical Tool Call and physical invocation identities plus the available Tenant, Workspace, Principal, Run, Agent, revision, correlation and argument context. It does not implement automatic retries, provider idempotency or exactly-once effects.

STDIO environment values are not persisted. `environmentReferences` maps child-process variable names to host configuration keys, resolved only when connecting. OAuth, a durable secret store, scheduled polling, MCP Resources and MCP Prompts are outside this iteration.
