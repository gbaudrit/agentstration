# ADR-0181: Console context is one exact administrative scope

Status: Accepted — 2026-10-22

## Context

The Console historically selected only a Workspace. Hierarchical resources later allowed Instance and Tenant ownership, so platform and Tenant administrators could inspect ancestor-owned definitions while the chrome still presented a business Workspace. That presentation conflated the principal's administrative authority, the owner scope being administered, the Workspace consuming a reusable definition, and the Workspace retaining execution state.

Three independent selectors would permit contradictory Instance, Tenant, and Workspace combinations. Reusing the internal `System` access mode for Instance administration would also expose every owner scope rather than the exact `/instance` catalogue. Finally, administrator authority must not turn into implicit `discover` access while the administrator is operating inside a business Workspace.

## Decision

- The Console carries one typed context selection: Instance, one authorized Tenant, or one authorized Workspace. The selector presents the hierarchy and persists the exact canonical `ResourceScopeRef` through a protected cookie, an API request header, and a navigation query value.
- Instance administration is an exact `/instance` access mode. Tenant administration is an exact `/tenants/{id}` access mode distinct from the internal hierarchical Tenant execution mode. The unrestricted `System` mode remains reserved for trusted background operations and bootstrap.
- The selected administrative context and its operational Workspace are separate values. The operational Workspace remains available to application services that need Workspace-owned drafts or, once the topology in #857 is delivered, durable execution placement. Selecting an administrative context does not change resource ownership.
- Context choices are derived from the authenticated principal's platform authorization, Tenant authorization, and accessible Workspaces. An absent choice cannot be entered by submitting its scope directly.
- In a Workspace context, same-Workspace resources are discoverable through ordinary RBAC. Ancestor-owned resources are returned by catalogues and reference resolution only when the access overlay grants `discover` directly to that Workspace or propagates it from an allowed ancestor. `discover` does not imply `use`.
- Flow discovery follows the same overlay rule as generic Management resources even though Flow definitions use their own repository.
- The combined Web host and the independent Console BFF propagate the same exact context. Context changes prompt before abandoning a page that advertises unsaved changes.

## Consequences

- The application chrome can accurately identify whether the operator is administering Instance, Tenant, or Workspace resources.
- Platform-administrator authority no longer causes hidden Instance or Tenant resources to appear in an ordinary Workspace catalogue.
- Exact administrative modes add explicit branches to authorization and storage scoping, but avoid weakening the existing internal `System` and Tenant modes.
- Deep links and browser history include the selected scope. Invalid or no-longer-authorized selections fail closed and clear stale context state.
- Instance and Tenant execution buttons require an explicit operational destination supplied by the governed topology in #857; this ADR does not create that topology or direct Instance/Tenant Run storage.
