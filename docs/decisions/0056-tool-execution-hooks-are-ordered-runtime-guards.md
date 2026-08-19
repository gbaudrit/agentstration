# ADR-0056: Tool execution hooks are ordered Runtime guards

## Status

Accepted

## Context

ADR-0055 establishes one mandatory provider-neutral Tool execution boundary and durable lifecycle projections. Lifecycle sinks intentionally observe execution without changing whether a provider call occurs. Agentstration also needs a separate extension point for cross-cutting Runtime decisions before later Management resources select and configure policies per Workspace, Agent or Tool.

The first hook contract must preserve MAF durable approval, provider diagnostics, cancellation, workspace scope and at-least-once execution without prematurely introducing input/output mutation, a generic policy engine or arbitrary middleware that can replace the provider invoker.

## Decision

The Tool execution pipeline runs locally registered `IToolExecutionHook` implementations between `ToolCallStarted` persistence and `IToolInvoker`:

```text
MAF approval, when required
  -> Tool Execution Pipeline
  -> ToolCallStarted projection
  -> ordered BeforeInvoke hooks
  -> provider-specific IToolInvoker
  -> reverse-order AfterInvoke notifications
  -> terminal Tool Call projection
```

Hooks are ordered first by integer `Order`, then by ordinal `Id`. Duplicate hook identifiers are rejected when the pipeline is constructed. Pre-invocation hooks return either `Allow` or a denial with a stable code and diagnostic message. A denial stops the chain before `IToolInvoker` and surfaces as `ToolExecutionDeniedException`; projections distinguish `Denied`, `Hook`, `Provider` and `Cancelled` failures.

Only hooks whose pre-invocation phase was entered receive a terminal notification. Notifications unwind in reverse order and expose the immutable execution context plus outcome, time, duration and error diagnostics. They do not receive the provider result and cannot mutate arguments or results. All entered terminal hooks are attempted even if one fails.

Hook cancellation is propagated unchanged. A hook failure on an otherwise successful provider call fails the governed call and can cause an at-least-once replay. A terminal hook failure while handling an existing provider failure is attached diagnostically and never replaces the original provider exception.

MAF `ApprovalRequiredAIFunction` remains outside and before this chain. Hooks cannot request or emulate durable approval. Every physical retry or resume invocation runs the hook chain again, even when it retains the same logical `ToolCallId`.

## Consequences

- Direct Runtime Runs and MAF Flow orchestrations execute the same ordered hook chain.
- Local or standalone hosts can register concrete hooks through dependency injection without a Management Plane dependency.
- Hooks fail closed before provider invocation and denial is observable separately from provider failure.
- Hook implementations must tolerate at-least-once execution; this contract does not make hook side effects exactly-once.
- Workspace/agent/tool selection through bounded Management resources is specified by ADR-0057. DLP/PII, quotas, result redaction, argument/result mutation, remote hooks and a generic policy language remain future work.
