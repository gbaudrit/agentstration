# ADR-0055 — Entry owns Workplace execution presentation

- Status: Accepted
- Date: 2026-08-18

## Context

Flow Runs now expose durable activities, participant turns, input requests, results, and artifacts. Rendering those capabilities directly from Flow topology or orchestration strategy would couple the end-user Workplace to Runtime and would force one experience on every Entry targeting the same Flow.

## Decision

Flow remains a complete provider-neutral execution capability. Entry owns the desired user experience, and Workplace projects durable Work state according to `Entry.presentation`.

The first execution-presentation settings cover participant visibility, progress visibility, inline Task display, and enriched Result display. Their compact defaults present one Agentstration assistant: participant mechanics are hidden, functional progress is compact, Task cards are contextual, and text results are carried by conversation messages. Profiles are deferred; the explicit settings are designed to become profile overrides later.

Workplace builds a presentation-only unified timeline from existing `ConversationMessage`, `WorkTaskActivity`, `PendingAction`, `WorkTask`, `WorkTaskResult`, and `WorkTaskArtifact` records. Participant turns are projected durably as attributed ConversationMessages so presentation can hide or expose them without querying Flow topology. Flow input requests continue to project to the existing PendingAction and remain Flow-owned.

## Consequences

- Two Entries can target the same immutable Flow version and render different experiences.
- Workplace has no strategy-specific views and does not interpret MAF checkpoints, routers, handoffs, or node identifiers.
- Task details and FlowRun diagnostics retain their more technical views.
- No PresentationEvent or profile engine is introduced in this increment.
