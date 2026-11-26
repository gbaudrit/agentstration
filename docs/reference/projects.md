# Workspace Projects

A Project groups existing private WorkPlans and functional tasks in one Workspace. It does not change their owner, plan revisions, execution or acceptance. Workspace remains the execution/authorization boundary; every Project operation also requires the authenticated Principal that owns the Project and its associated work. Several Projects may reference the same owned plan or task. No repository or external connector is required.

## Create and query

Send `POST /api/workspaces/{workspaceName}/projects` with run-submission permission:

```json
{
  "id": "60000000-0000-0000-0000-000000000001",
  "commandId": "70000000-0000-0000-0000-000000000001",
  "name": "operations",
  "namespace": "default",
  "definition": {
    "displayName": "Operations",
    "description": "Collect and review operational evidence.",
    "planIds": [],
    "taskIds": []
  }
}
```

Success returns `201 Created`, a `Location` and the current Project, including association IDs, version and timestamps. Names are immutable lowercase ASCII identifiers up to 128 characters; namespace uses the ordinary resource namespace syntax. Display names are required and bounded to 512 characters, descriptions to 2,000. Project metadata is user-authored content.

`GET .../projects/{projectId}` returns the Project and its explicit work associations with run-read permission. Read a referenced plan or task through its canonical Work API. `GET .../projects?skip=0&take=25` returns a private bounded page ordered by namespace, name and ID. `take` is 1–100 and `skip` is nonnegative. `hasMore` is explicit; no approximate or implicit total is returned. Unauthorized/foreign Projects remain inaccessible and foreign references fail without revealing content.

## Update associations

Send `PUT .../projects/{projectId}` with execution permission:

```json
{
  "commandId": "70000000-0000-0000-0000-000000000002",
  "expectedVersion": 1,
  "definition": {
    "displayName": "Operations",
    "description": "Two related delivery plans.",
    "planIds": ["30000000-0000-0000-0000-000000000001"],
    "taskIds": ["10000000-0000-0000-0000-000000000002"]
  }
}
```

Both collections are explicit replacement sets, bounded to 100 unique nonempty IDs each. Missing collections mean empty sets. Plans/tasks must already exist under the same Workspace and Principal. Direct task associations are independent from tasks reached through a referenced plan; membership never implicitly adopts a tree or rewrites a WorkPlan.

Updates atomically check the Project version and observed owned work versions. Stale writes, duplicate names and conflicting command reuse return Problem Details `409`. Missing/inaccessible work returns `404`; invalid definitions/pages return `400`. Bodies are limited to 64 KiB. Each Project retains at most 256 command receipts; identical replay still returns current state at the limit and preserves subsequent edits. SQLite initialization upgrades existing stores without replacing work. PostgreSQL has an equivalent migration; live PostgreSQL validation is opt-in.

## Workspace Bootstrap

The bundled `workspace-project` profile creates an ordinary editable `operations` Project without bindings. Select an explicit authorized Workspace target through the existing Bootstrap API/administration flow. Its resource manifest is:

```yaml
apiVersion: agentstration.io/v1
kind: Project
metadata:
  name: operations
definition:
  displayName: Operations
  description: Groups local plans and tasks in this Workspace.
```

Optional `definition.id` supplies a portable Project UUID; `planIds` and `taskIds` associate existing work. Otherwise Bootstrap derives a stable local ID. Reapplication skips an existing private name, preserving edits. An explicit ID different from that existing Project conflicts. Instance/Tenant profiles and foreign targets are rejected. Planning and application both use the Project service, and ordinary Bootstrap planning references can resolve the namespaced Project.

Grouping and Bootstrap creation are #950; context references and admission snapshots are #951. Project sharing and Workspace seed ingestion remain #1345/#1347. See [ADR-0269](../decisions/0269-projects-group-private-work-without-changing-workspace-ownership.md) and [ADR-0270](../decisions/0270-project-context-is-authorized-and-captured-at-task-admission.md).

## Bind exact context references

Create/update definitions can include `bindings`. Every reference is explicit and pins an existing resource; this example assumes the selected ToolProvider has already been installed and authorized:

```json
{
  "displayName": "Operations",
  "planIds": ["30000000-0000-0000-0000-000000000001"],
  "taskIds": [],
  "bindings": [
    {
      "name": "sharing",
      "purpose": 1,
      "target": {
        "address": {
          "scopeRef": "/instance",
          "namespace": "default",
          "kind": "ToolProvider",
          "name": "git-operations"
        },
        "uid": "80000000-0000-0000-0000-000000000001",
        "generation": 1
      }
    }
  ]
}
```

Purpose values are `Context = 0`, `Sharing = 1`, `Repository = 2`. Up to 32 unique names are supported. Sharing has at most one binding and selects a ToolProvider; Repository has at most one and selects a ToolProvider or ConnectedService. Context may also select an immutable SourceVersion. Supply the exact UID/generation and `version` when the resource identity has one. No credentials, arbitrary provider options or Secret targets are accepted. Ordinary Bootstrap Project manifests accept the same bindings, with camel-case purpose names such as `sharing` in YAML.

Create/update validates the selected ownership tree, current resource identity, Discover permission and Use permission. Missing or undiscoverable references return `work_project_binding_unavailable` (`404`); denied Use is `403`, and an accessible stale identity is `409`. Invalid reference shape/purpose is `400`. Removing or explicitly replacing a broken binding remains possible through the usual version-checked edit; no template or automatic retargeting updates it.

`GET .../projects/{projectId}/context` returns the current Project version and per-binding `available`/`code` diagnostics. A resolved reference has code `resolved`. Availability describes current identity/access metadata, not remote connectivity, health or supported operations. Actual consumers retain their normal capability, health and authorization checks. Resource bodies and protected values are never included.

## Capture context for execution

Include `projectId` in the [WorkPlan execution configuration](work-plans.md#execute-an-active-plan). The Project must belong to the caller and explicitly reference the plan. The scheduler uses the same admission path for Project and standalone work.

Every admitted attempt exposes `projectContext`: Project ID, version, digest, captured exact bindings, ETags, Discover/Use decision reasons and capture time. A concurrent Project edit rejects the task claim with `work_project_context_changed` (`409`); after rereading, admission can observe the newer version. A missing association returns `work_project_plan_not_associated` (`409`). Current inaccessible/stale bindings block new admission with the same bounded diagnostics as authoring.

Changing Project v1 to v2 does not alter an existing attempt. Later admissions and explicit retries capture v2. Submission recovery and server restart keep the admitted v1 evidence. Revoking access changes current diagnostics and blocks new admission; earlier evidence remains available. A resource consumer must reauthorize its captured reference and fail explicitly if it is no longer usable, without selecting a replacement. References are not automatically interpolated into Flow input or prompts.

Old Projects with no bindings and standalone plans with no Project remain valid and replay their existing commands. These optional JSON fields require no new SQLite or PostgreSQL migration.
