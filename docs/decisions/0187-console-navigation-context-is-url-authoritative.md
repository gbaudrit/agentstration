# ADR-0187: Console navigation context is URL-authoritative

Status: Accepted - 2026-10-30

## Context

ADR-0184 established that the Console operates in one exact Instance, Tenant, or Workspace administrative context. Its initial implementation also persisted that selection in host cookies. Cookies are shared by tabs and, on localhost, by Console instances running on different ports. A context switch in one tab or development worktree could therefore change the meaning of another tab's navigation and API requests. Cross-scope links also carried only the resource owner `scopeRef`, so opening a Workspace-owned resource from an Instance page could retain the wrong administrative context.

Resource ownership and navigation context are related but different concerns. `scopeRef` must continue to identify the exact owner from which a resource is read. The Console context must identify the exact administrative scope in which the destination page and all of its API calls execute.

## Decision

- The Console query parameter `context` is the authoritative per-document administrative context. It contains one canonical `ResourceScopeRef`.
- Console HTTP clients copy that value to `X-Agentstration-Context` on API requests in both the combined Web host and the separate Console BFF deployment.
- Console context selection validates the requested scope but does not persist it in `agentstration.context` or `agentstration.workspace` cookies. Legacy Workspace cookies remain available to Workplace and non-Console paths, but do not override an explicit Console context.
- When a Console URL has no `context`, the server may resolve the principal's default authorized context. The layout then normalizes the browser URL so subsequent navigation, refresh, duplication, and history entries remain explicit.
- An explicit malformed or unauthorized context fails closed with `403 Forbidden`. It never falls back to a different authorized scope and never clears or rewrites another tab's context.
- Cross-scope resource links carry both `scopeRef` for the resource owner and `context` for the destination administrative context. Opening such a link in another tab therefore leaves the source tab unchanged.
- Authentication cookies and BFF delegation tokens remain host concerns. They do not carry navigation context.

This decision supersedes only ADR-0184's cookie-persistence mechanism. ADR-0184's exact-scope authorization model and separation between administrative context, resource ownership, and operational Workspace remain in force.

## Consequences

- Tabs, copied deep links, browser history, and local worktrees can hold independent Console contexts.
- URLs are longer, but they are reproducible and state their administrative meaning explicitly.
- API clients created by Console components require access to the current navigation URI so they can attach the context header.
- Resource URL builders must distinguish owner scope from destination context; omitting one can no longer be compensated by process-wide cookie state.
- Existing Workplace Workspace-selection behavior is preserved while the Console stops depending on that shared cookie.
