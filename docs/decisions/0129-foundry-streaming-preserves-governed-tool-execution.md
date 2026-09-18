# ADR-0122: Foundry streaming preserves governed Tool execution

Status: Accepted — 2026-09-18

## Context

ADR-0121 established bounded non-streaming Foundry chat through AEP. FR-450 adds streaming and Tool calls while preserving the Agentstration Tool execution and approval boundary of ADR-0055.

## Decision

- The Foundry extension advertises AEP streaming and Tools. Chat deployments advertise streaming; they advertise Tools only when discovery affirmatively reports Tool calling. It maps OpenAI/v1 Chat Completions SSE deltas to ordered AEP text, Tool calls, usage and terminal finish reason. It does not invoke Tools.
- AEP Tool definitions, assistant Tool calls and Tool results map to Foundry request messages. Returned Tool calls travel through the existing AEP to `IChatClient` adapter and MAF, which continues to invoke only Agentstration's governed Tool pipeline and approval workflow.
- Require a provider completion marker and a valid finish reason. Bound request bytes, stream bytes, line bytes, event count, Tool count and Tool argument bytes. Reject malformed or interrupted streams, including those that end after emitting text, without replaying the request. Caller cancellation propagates; the configured request timeout remains in force for the entire stream.
- Preserve the deployment name as the AEP model identity and carry streaming token usage through the AEP to Microsoft.Extensions.AI adapter.

## Consequences

Streaming Foundry responses use the same Tool governance as other AEP providers. An incomplete stream fails instead of silently appearing successful, and no automatic retry can duplicate text or effects after an event has been observed. Structured output, explicit reasoning, Foundry-side Tool execution and Foundry-hosted Agents remain outside this increment.
