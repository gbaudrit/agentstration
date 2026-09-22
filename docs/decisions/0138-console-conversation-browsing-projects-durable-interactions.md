# ADR-0138 — Console conversation browsing projects durable Interactions

## Status

Accepted

## Context

ADR-0137 gives Console a generic route that can replay a durable Entry Interaction when its identity is known. Users still need a discoverable way to return after navigating elsewhere without introducing a second conversation index or copying message state into Console.

## Decision

Console exposes a Conversations destination for the currently selected Workspace. It reads the existing bounded Work Interaction list, intersects it with canonical executable Console Entry discovery, and presents the 50 most recently active authorized conversations. Items whose Entry is missing, unauthorized, no longer Console-exposed, unavailable, or ambiguous are omitted without exposing their metadata.

The first user message may provide a whitespace-normalized title capped at 96 characters. The list does not log it, load full message histories, or persist another projection. Entry display name, owner Workspace, Interaction status, last activity, and pending-action presence come from existing authorized contracts.

Selecting an item uses the ADR-0137 route with the owner Workspace, Entry namespace and name, and durable Interaction ID. Work API rechecks Workspace authorization and principal ownership for both list and replay. Switching the Console Workspace changes the list scope rather than aggregating data across administrative contexts.

## Consequences

Conversation discovery remains bounded, Workspace-isolated, principal-owned, and offline-capable with the existing SQLite path. Closed and terminal conversations remain resumable while their Entry remains executable and Console-exposed. Cross-Workspace aggregation, deletion, renaming, sharing, export, and a Console-owned conversation store remain outside this increment.
