# ADR-0122: The official Assistant is a Workspace-owned composition

## Status

Accepted

## Context

Console and Workplace can present Entries owned by another readable Workspace. Product help, diagnostics, and Resource Planning therefore need one reusable Assistant boundary without moving Agents, Flows, Tools, Entries, conversations, or execution state to Tenant scope or embedding orchestration in either UI.

## Decision

Each Tenant may provision one ordinary Workspace with the reserved technical name `agentstration-assistant`. The Workspace is the ownership and execution boundary; its name is stable product identity, not an authorization shortcut. Access still follows explicit Tenant/Workspace membership and role rules.

The public capability boundary is the Workspace-owned `ask-agentstration` Entry. It targets the `assistant-router` Flow, which delegates to active child Flows named `assistant-help`, `assistant-diagnostics`, `resource-planning`, and `assistant-fallback`. Child capabilities remain independently invokable and replaceable. Router inputs contain presentation-neutral user intent, and child outputs pass through unchanged. Flow Run child links and the existing causality read model provide observability.

Entry exposure controls where the Assistant is presented. It never changes the owning Workspace or grants visibility to sibling resources. Console and Workplace remain optional consumers and contain no capability-specific routing.

Model and Runtime Profiles are installation bindings. Governed Tools are assigned explicitly to the specialist Agents that need them. The Assistant never receives generic Management CRUD or database access.

## Consequences

- The Assistant executes without Console and can be reused by Workplace or future surfaces.
- A stable Entry survives replacement or version changes in individual capabilities.
- Installation is a two-stage existing-bootstrap operation: provision the Tenant-owned Workspace, then install the Workspace-scoped Pack into it.
- Removing presentation exposure does not remove the Assistant resources or their history.
- Generic cross-Workspace references, Tenant-owned agentic resources, and UI-owned routing remain out of scope.
