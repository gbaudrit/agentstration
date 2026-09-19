# Agentstration Resource Planning Pack

This official Workspace-scoped Pack installs the functional planning composition introduced by FR #333. It turns a natural-language goal into a governed Resource Planning v1 plan, preserves clarification boundaries, and can preview a materialized ChangeSet. It never applies the ChangeSet.

Bind `conversational-model` to a compatible ModelProfile and `local-runtime` to a local RuntimeProfile when installing the Pack. The installer projects Agentstration's built-in planning Tools into the Workspace `default` namespace before validating the Agents, so installation does not depend on visiting a management page first.

Invoke the active `resource-planning` Flow directly with a `goal`. The Agentstration Assistant Pack can also route an explicit planning request to this Flow when both official Packs are installed. Review and approve any proposed ChangeSet through the normal Resource Planning governance surface; no Pack Agent has apply authority.
