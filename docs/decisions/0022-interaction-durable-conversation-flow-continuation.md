# ADR-0022: Interaction as durable conversation and FlowRun continuation

## Status

Accepted.

## Context

The initial Workplace vertical treated Entry submission, pending clarification, Task execution, and result delivery as a mostly linear lifecycle. A completed WorkTask also left no application capability behind the existing message endpoint: a follow-up could be stored, but it could not start new work. Reopening a terminal FlowRun would violate published-run immutability and would mix technical resumption with product-level conversation continuation.

## Decision

An Entry always opens a durable Interaction. The Interaction is the user-facing conversation and remains `Idle` after successful work, ready for another message, until explicitly closed. `Processing` means work is active and `WaitingForUser` means a blocking PendingAction must be answered. Completing a Task or FlowRun does not complete the Interaction.

Technical resumption and conversational continuation remain distinct:

- a PendingAction resumes the same suspended execution with its single-use server-side continuation state;
- a message after a terminal FlowRun creates a new immutable FlowRun with `ParentFlowRunId`, `InteractionId`, public `WorkTaskId`, and `TriggerMessageId` correlation.

The application builds an `InteractionContinuationContext` projection containing identifiers, recent functional messages, and result/artifact references. It never passes EF entities, runtime objects, provider/model configuration, technical prompts, or traces. An Entry may configure an optional continuation target; otherwise its initial target is reused.

For the MVP, a transformation or refinement creates a continuation WorkItem as a technical execution child of the existing public WorkTask. The child WorkItem and FlowRun remain distinct immutable executions, while Workplace projects their activities, successive results, and versioned artifacts under the original Task identifier. Continuation WorkItems are not listed as additional user Tasks.

The conversation composer remains visible after immediate responses, while work runs, and after results. It is disabled with an explanation only for blocking PendingActions, a closed Interaction, or an unavailable operation. Simple choice and confirmation PendingActions are rendered inline and submit on one click; complex structured data can still use the existing field renderer inside the thread.

`prepare-report` uses progressive disclosure and starts with the `standard` default. `guided-request` remains the deterministic PendingAction demonstration.

## Consequences

Workplace can produce an initial result, accept a follow-up, correlate a new FlowRun to the previous terminal run, and retain all output versions without introducing users, tenants, authentication, a new broker, or a second conversation engine. REST remains authoritative and SignalR targets the active Interaction and Task.

The public WorkTask is now an aggregate projection over one anchor WorkItem and zero or more continuation WorkItems. Task commands target the most recent execution. This grouping rule is intentionally application-owned and is covered by offline functional tests.

Raw PendingAction resume tokens remain browser capabilities and are not persisted in Interaction payloads or public events. This ADR does not introduce account-bound recovery of those capabilities.
