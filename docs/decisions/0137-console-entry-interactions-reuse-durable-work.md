# ADR-0137 — Console Entry interactions reuse durable Work

## Status

Accepted

## Context

The Console command palette can navigate an unmatched query to one explicitly selected primary Console Entry. The destination must support every Entry presentation without creating an Assistant-specific execution path, losing durable state, or executing in the Workspace merely selected for administration.

## Decision

Console hosts one generic Entry interaction route identified by the Entry owner's Workspace ID, namespace, and name. It resolves the Entry through canonical Console discovery and submits through the existing Entry interaction endpoint. Continuation messages, pending-action responses, cancellation, activities, results, artifacts, and replay all use the existing Workspace-scoped Work API contracts and realtime events.

An initial palette query is mapped unchanged to the Entry's primary input. It is submitted automatically only when that field is string-compatible and no additional required field exists; otherwise the generic Entry renderer prefills the value and lets the user complete the form. Direct navigation without a query never submits.

The replay URL contains the durable Interaction ID. Every read and mutation uses the route's owner Workspace, and a loaded Interaction must match the resolved Entry identity. Missing authorization, missing discovery, disabled targets, and unavailable targets fail closed without a Console-only resolver.

## Consequences

Prompt, form, conversation, action, progress, result, artifact, failure, and cancellation presentation continue to be driven by Entry and Work contracts shared with Workplace. The Console adds no official-Assistant type and persists no parallel conversation state. Future Entry kinds can participate by extending the shared renderer and contracts rather than this route.
