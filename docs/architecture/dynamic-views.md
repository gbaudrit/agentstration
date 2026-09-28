# Dynamic architecture views

These Mermaid sequence diagrams describe the principal implemented execution paths. They use names from the [interactive structural model](interactive.mdx), but do not redefine system, container, component, or data ownership. The diagrams intentionally stop at architectural boundaries rather than tracing methods or every event.

## Submit Work

Entry, Trigger, REST, Console, and Flow-backed Tool callers converge on the same root submission boundary. Trusted execution scope comes from the authenticated context, never from caller-controlled arguments.

```mermaid
sequenceDiagram
    participant Caller
    participant API as API transport
    participant Submit as Root Flow submission
    participant Work as Work application
    participant Flow as Flow engine
    Caller->>API: Submit executable Entry or Flow target
    API->>Submit: Authorized request and idempotency context
    Submit->>Submit: Resolve active reference to exact published version
    Submit->>Work: Create or recover idempotent Work Item
    Submit->>Flow: Create or recover deterministic root Flow Run
    Flow-->>Submit: Durable Flow Run identity
    Work-->>Submit: Durable Work identity
    Submit-->>API: Accepted identities
    API-->>Caller: 202 Accepted and durable references
```

Structural anchors: **API transport**, **Work and Workplace application**, and **Flow engine** in the L3 view. Decisions: [ADR-0024](../decisions/0024-entries-always-target-executable-flows.md), [ADR-0053](../decisions/0053-workspace-scope-is-part-of-durable-identity.md), and [ADR-0106](../decisions/0106-tool-definitions-publish-flow-backed-mcp-tools.md).

## Execute Flow

A Flow Run owns graph progress and durable Flow events. Work owns the functional lifecycle; Runtime owns technical Agent invocation; the Tool pipeline owns governed Tool invocation.

```mermaid
sequenceDiagram
    participant Worker as Flow worker
    participant Flow as Flow engine
    participant FlowData as Flow data
    participant Runtime as Runtime execution
    participant Tools as Tool execution pipeline
    participant Work as Work application
    Worker->>Flow: Claim pending Flow Run
    Flow->>FlowData: Load exact validated snapshot and replay state
    loop Typed graph steps
        alt Flow step
            Flow->>FlowData: Create deterministic child Flow Run
            Flow->>FlowData: Suspend parent until child completes
        else Agent step
            Flow->>Runtime: Create technical Runtime Run
            Runtime-->>Flow: Normalized result and events
        else Tool step
            Flow->>Tools: Execute governed Tool attempt
            Tools-->>Flow: Result and governance events
        else Deterministic step
            Flow->>Flow: Evaluate mapping, routing, or transform
        end
        Flow->>FlowData: Persist transition and differential events
    end
    Flow->>Work: Project functional progress or terminal result
```

Structural anchors: **Flow engine**, **Runtime execution**, **Tool execution pipeline**, and **Work and Workplace application**. Decisions: [ADR-0010](../decisions/0010-independent-flow-module.md), [ADR-0019](../decisions/0019-flow-run-resource-and-console.md), [ADR-0048](../decisions/0048-flow-runs-carry-a-durable-execution-scope.md), [ADR-0054](../decisions/0054-durable-interactive-flow-execution.md), and [ADR-0105](../decisions/0105-flows-compose-flows-and-governed-tools.md).

## Execute Agent

Direct Console/API execution and Flow Agent steps create Runtime Runs without transferring functional Work ownership to Runtime.

```mermaid
sequenceDiagram
    participant Caller as Flow or API caller
    participant Runtime as Runtime execution
    participant Resources as Resource-family services
    participant RuntimeData as Runtime data
    participant Adapter as Agent Framework adapter
    participant Model as Resolved chat client
    Caller->>Runtime: Create Run for exact Agent identity
    Runtime->>Resources: Resolve Agent revision, deployment, profiles, and Tools
    Runtime->>RuntimeData: Persist immutable scope and enqueue Run
    Runtime->>Adapter: Materialize reconstructible agent
    Adapter->>Model: Invoke with effective capabilities and options
    Model-->>Adapter: Response, usage, and Tool activity
    Adapter-->>Runtime: Provider-neutral execution events
    Runtime->>RuntimeData: Persist ordered events and terminal result
    Runtime-->>Caller: Durable status and result
```

Structural anchors: **Runtime execution**, **Resource-family services**, and **Runtime data**. Decisions: [ADR-0008](../decisions/0008-reconstructible-maf-runtime.md), [ADR-0012](../decisions/0012-runtime-run-resource.md), and [ADR-0017](../decisions/0017-canonical-runtime-model-options-and-capabilities.md).

## Execute Tool

Flow Tool steps and Agent tool calls share one governed execution pipeline. Logical and physical-attempt identities, trusted execution scope, policy decisions, and bounded retention stay under Agentstration control.

```mermaid
sequenceDiagram
    participant Caller as Flow engine or Runtime adapter
    participant Tools as Tool execution pipeline
    participant Resources as Resource-family services
    participant Hooks as Ordered policy hooks
    participant Provider as Internal or MCP provider
    Caller->>Tools: Invoke Tool with trusted execution context
    Tools->>Resources: Resolve Tool, provider, assignment, and policy
    Tools->>Hooks: Evaluate ordered before-invocation hooks
    opt Approval required
        Tools-->>Caller: Durable InputRequest suspension
        Caller->>Tools: Resume with authorized response
    end
    Tools->>Provider: Execute bounded provider call
    Provider-->>Tools: Tool result or normalized failure
    Tools->>Hooks: Evaluate ordered after-invocation hooks
    Tools-->>Caller: Result and governance events
```

Structural anchors: **Tool execution pipeline**, **Resource-family services**, **Flow engine**, and **Runtime execution**. Decisions: [ADR-0055](../decisions/0055-agentstration-owns-tool-execution-boundary.md), [ADR-0056](../decisions/0056-tool-execution-hooks-are-ordered-runtime-guards.md), [ADR-0058](../decisions/0058-tool-governance-decisions-are-traced-per-physical-attempt.md), and [ADR-0059](../decisions/0059-tool-arguments-require-explicit-bounded-retention.md).

## Invoke AEP or MCP

AEP owns extension identity and contribution discovery, plus model contribution invocation. MCP remains authoritative for external Tool schema discovery and Tool calls; a direct MCP provider bypasses AEP.

```mermaid
sequenceDiagram
    participant Runtime as Runtime execution
    participant Tools as Tool execution pipeline
    participant Extension as AEP extension
    participant Provider as Provider-specific service
    participant MCP as MCP server
    alt Model contribution
        Runtime->>Extension: AEP chat request with bounded options
        Extension->>Provider: Provider-native inference request
        Provider-->>Extension: Provider-native response or stream
        Extension-->>Runtime: Normalized AEP response or updates
    else AEP-backed Tool contribution
        Tools->>Extension: Discover bounded Tool contribution
        Extension-->>Tools: MCP server declaration
        Tools->>MCP: tools/list or tools/call
        MCP-->>Tools: MCP schemas, result, or protocol error
    else Direct MCP provider
        Tools->>MCP: tools/list or tools/call
        MCP-->>Tools: MCP schemas, result, or protocol error
    end
```

Structural anchors: **Runtime execution**, **Tool execution pipeline**, the external **AEP extensions** system, and optional provider services. Decisions: [ADR-0026](../decisions/0026-out-of-process-aep-model-provider-extensions.md), [ADR-0027](../decisions/0027-aep-tool-contributions-resolve-to-mcp.md), [ADR-0028](../decisions/0028-tool-providers-governed-catalog.md), [ADR-0065](../decisions/0065-model-providers-bind-extension-contributions.md), and [ADR-0094](../decisions/0094-aep-extensions-initiate-enrollment.md).
