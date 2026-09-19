# Official Agentstration Assistant

The official Assistant is installed in a dedicated Workspace and exposed through the stable `ask-agentstration` Entry. Installation is deliberately two-stage so an operator chooses the owning Tenant and explicitly selects the ModelProfile and local RuntimeProfile.

## Install

1. Apply the tenant-scoped `agentstration-assistant-workspace` bootstrap profile to the intended Tenant. This creates the `agentstration-assistant` Workspace without adding members or changing a user's default Workspace.
2. Grant the intended operators access through the normal Workspace membership and role process.
3. In that Workspace, apply the workspace-scoped `agentstration-assistant` profile. Bind `assistant-model` to a compatible ModelProfile and `assistant-runtime` to a local RuntimeProfile.
4. Verify that both official Packs are installed and that the active `ask-agentstration` Entry is discoverable from Console and the Workplace owning-space surface.

The profile installs Resource Planning first and the Assistant second. Reapplying the same versions is idempotent. Installation is local-first: the profile reads the committed Pack archives and does not require a remote catalog.

## Upgrade and reseed

Build updated archives with `pwsh scripts/packs/build-official-assistant-packs.ps1`, increment each changed Pack version, and review the archive diff before committing it. The bootstrap handler intentionally reports a version mismatch as a conflict instead of silently replacing managed resources. Upgrade through the Pack management workflow, where the proposed resource changes can be previewed and reviewed. Reapply the profile afterward to confirm the target versions are present.

For a clean reseed, uninstall the two Packs through Pack management, then reapply the workspace profile. Remove the Assistant Pack before Resource Planning because the Assistant has the consumer-side cross-Pack reference.

## Customize or disable

Do not edit Pack-managed resources in place. Fork the Pack through Pack management, give the fork a distinct identity and namespace, then change the Entry, routing, prompts, or bindings there. Disable presentation access by unpublishing or replacing the Entry; disabling a presentation consumer does not alter Assistant ownership.

To remove the official capability, uninstall `agentstration/assistant`, then `agentstration/resource-planning`. The dedicated Workspace can remain for audit history or be removed later through the normal governed Workspace lifecycle. Membership, role, and default-Workspace changes remain explicit operator actions throughout.
