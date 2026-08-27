# ADR-0060 — Entry owns Workplace execution presentation

- Status: Accepted
- Date: 2026-08-18

## Context

Flow Runs now expose durable activities, participant turns, input requests, results, and artifacts. Rendering those capabilities directly from Flow topology or orchestration strategy would couple the end-user Workplace to Runtime and would force one experience on every Entry targeting the same Flow.

## Decision

Flow remains a complete provider-neutral execution capability. Entry owns the desired user experience, and Workplace projects durable Work state according to `Entry.presentation`.

The first execution-presentation settings cover participant visibility, progress visibility, inline Task display, and enriched Result display. Their compact defaults present one Agentstration assistant: participant mechanics are hidden, functional progress is compact, Task references are contextual, and text results are carried by conversation messages. Profiles are deferred; the explicit settings are designed to become profile overrides later.

Workplace builds a presentation-only unified timeline from existing `ConversationMessage`, `WorkTaskActivity`, `PendingAction`, `WorkTask`, `WorkTaskResult`, and `WorkTaskArtifact` records. Participant turns are projected durably as attributed ConversationMessages so presentation can hide or expose them without querying Flow topology. Flow input requests continue to project to the existing PendingAction and remain Flow-owned.

In conversation mode, a PendingAction is rendered as an Agentstration turn with inline text, choice, or confirmation controls rather than as an operations panel. Compact progress shows the current functional activity and suppresses lifecycle markers already conveyed by the pending action, final answer, result, or artifact; detailed progress retains the full activity history.

Participant turn boundaries are projected durably as generic `ProgressStarted` and `ProgressCompleted` WorkTaskActivities. Work stores functional labels and participant correlation metadata, never Flow node names or orchestration details. Workplace keeps the generic labels when participants are hidden and composes participant-aware labels only when `Entry.presentation.participants.visibility` is `visible`.

`task.display: auto` is resolved entirely by Workplace from durable Work evidence. Interrupted or actionable Tasks are always materialized; otherwise a compact inline Task reference appears only after a meaningful observed duration, multiple completed progress milestones, or multiple deliverables. Explicit `visible` and `hidden` settings remain authoritative. The detailed Task timeline collapses completed start/end pairs and distinguishes completed, current, and terminal activities.

The user-facing synthesis is carried by a ConversationMessage. Automatic presentation does not render WorkTaskResult payloads inside the end-user conversation, even when they contain additional execution metadata. An Entry may explicitly request visible Results; a Development host may instead expose the same payloads behind a collapsed diagnostic control that is not part of Entry configuration. Workplace projects only artifacts explicitly declared by WorkResult, and a textual result is not converted into a synthetic downloadable artifact.

## Consequences

- Two Entries can target the same immutable Flow version and render different experiences.
- Workplace has no strategy-specific views and does not interpret MAF checkpoints, routers, handoffs, or node identifiers.
- Task details and FlowRun diagnostics retain their more technical views.
- No PresentationEvent or profile engine is introduced in this increment.
