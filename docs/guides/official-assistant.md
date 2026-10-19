# Official Agentstration Assistant

The official Assistant is installed in a dedicated Workspace and exposed through the stable `ask-agentstration` Entry. Installation is deliberately two-stage so an operator chooses the owning Tenant and explicitly selects the ModelProfile and local RuntimeProfile.

## Install

1. Apply the tenant-scoped `agentstration-assistant-workspace` bootstrap profile to the intended Tenant. This creates the `agentstration-assistant` Workspace without adding members or changing a user's default Workspace.
2. Grant the intended operators access through the normal Workspace membership and role process.
3. In that Workspace, apply the workspace-scoped `agentstration-assistant` profile. Bind `assistant-model` to a compatible ModelProfile and `assistant-runtime` to a local RuntimeProfile.
4. Verify that the active `ask-agentstration` Entry is discoverable from Console and the Workplace owning-space surface.

The profile creates the Resource Planning Agents and Flows first, followed by the Assistant Agents, Flows, and Entry. The resources use the stable `agentstration.resource-planning` and `agentstration.assistant` namespaces and remain ordinary editable Workspace resources. Reapplying the profile is idempotent under Bootstrap create/skip semantics and requires no remote catalog.

The Resource Planning Flow can also be invoked directly as `agentstration.resource-planning/resource-planning`; the Assistant router calls that same Flow for explicit planning requests.

## Reseed and customization

Bootstrap does not reconcile or overwrite existing resources. For a clean alpha reseed, remove the Assistant Entry and Flows, then its Agents, followed by the Resource Planning Flows and Agents, and reapply the profile. Existing durable Work and Flow Run history remains subject to the normal resource deletion guards.

The created resources are not Pack-managed and may be customized through their normal management surfaces. Reapplying the profile skips existing addresses and does not revert those changes. Disable presentation access by unpublishing or replacing the Entry; this does not remove the underlying Assistant or Resource Planning resources.

Official Pack distribution is deferred to #560 and #561. This alpha Bootstrap path does not provide Pack provenance, Pack update, Pack uninstall, or automatic adoption by those future Packs.
