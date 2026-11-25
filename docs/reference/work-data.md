# YAML Work data

The tracking repository already represents one project. Keep configuration, connections, harnesses
and all Flow definitions under `bootstrap/`. Author Work beneath `data/`, with no additional
`projects/` or Scenario resource. API bodies and private storage keep their existing JSON contracts.

## Explicit catalog

Paths in the API package and catalog are relative to the `data/` root. Only explicitly referenced
documents are imported. Extra unreferenced files never become work/configuration. A catalog is:

```yaml
apiVersion: agentstration.io/v1
kind: WorkCatalog
metadata:
  name: library-demo
definition:
  formatVersion: "1.0"
  projectId:
    value: bfae0f52-e531-4309-9fd6-799fdce7df44
  plans:
    - plans/search/plan.yaml
```

`plans/search/plan.yaml` declares stable IDs rather than deriving them from folders:

```yaml
apiVersion: agentstration.io/v1
kind: WorkPlan
metadata:
  name: search
definition:
  id:
    value: 28cc0c95-249a-4412-8309-334247df0c3e
  plan:
    intent: Implement title and author search
    taskIds:
      - value: 3553a210-68b2-4202-b783-13b581b66808
    dependencies: []
  taskTrees:
    - plans/search/taches/implementation.yaml
```

The referenced file describes the ordinary task-tree contract:

```yaml
apiVersion: agentstration.io/v1
kind: WorkTaskTree
metadata:
  name: implementation
definition:
  rootTaskId:
    value: 3553a210-68b2-4202-b783-13b581b66808
  tasks:
    - id:
        value: 3553a210-68b2-4202-b783-13b581b66808
      kind: atomic
      title: Implement search
      instruction: Follow the selected source issue and run the approved checks.
```

Optional `definition.issue` on a WorkPlan uses the existing portable issue-reference contract.
It is a declaration, not an acknowledged association: normal issue intake must still read the
source authority, check native dependencies and publish the unique plan association. An import
does not write to GitHub. Flow selection, native bindings, allocation, validation/activation and
execution remain their normal explicit operations against installed published resources; no Flow
definition is accepted in a Work document.

The bounded contract supports 16 plans, 100 distinct tasks, 128 files, 16 KiB per UTF-8 file and
256 KiB total. Paths are lowercase relative `.yaml` names without empty/dot/parent segments.
Strict parsing rejects duplicate/unknown keys, aliases/tags and unsupported envelopes. Tree and
dependency validation reuses Work. Diagnostics identify the source file and invariant error code.

## Review and import

Routes are under `/api/workspaces/{workspace}/projects/{projectId}/data`:

| Route | Behavior |
| --- | --- |
| `POST /preview` | Supply `{ "package": { "revision": null, "files": [{ "path": "catalog.yaml", "content": "..." }] } }`, or `{ "revision": null }` for the Project's protected sharing connector. Returns diagnostics, normalized selected plans, exact source and review digests. No writes. |
| `POST /imports` | Supply a stable `id`, the exact `reviewedDigest`, and the same package or the preview's pinned Git `revision`. Returns `202` with a private retained operation. |
| `GET /imports/{id}` | Reload retained imported-plan identities, status, source/review evidence and sanitized failure code. |
| `POST /imports/{id}/resume` | Explicitly resume the same frozen declarations and deterministic creation identities after partial failure/restart. |

Protected acquisition reads only the explicit catalog and references at one exact Git commit
through the existing governed Tool/Secret boundary. Uploaded packages need no provider; an uploaded
revision is caller-supplied provenance, whereas connector acquisition resolves the actual Git SHA.
Current Workspace/Principal and private Project ownership are checked on every operation.

Review pins exact source content, target, Project context and relevant local work observations.
Admission durably reserves the import and atomically guards those local observations before creating
work. Each completed plan is retained independently; a partial operation does not pretend rollback.
Retrying an uncertain response uses the same ID/input. Changed input conflicts. Infrastructure
exceptions remain failures and the frozen pending operation can be inspected/resumed.

Plans are imported as ordinary local drafts. Existing matching work is reused; divergent local
proposals or immutable trees conflict rather than being overwritten. A moved file requires fresh
source review but retains the same functional plan/task identities. Omission never deletes work.
No import activates, allocates, dispatches, accepts outcomes, publishes code or closes issues.

See [Project sharing](project-sharing.md), [WorkPlans](work-plans.md) and
[ADR-0276](../decisions/0276-yaml-work-imports-prepare-reviewed-drafts.md).

## Optional logical epics

The repository is already project-scoped. An optional layout is:

```text
bootstrap/
  profile.yaml
  flows/coding.yaml
data/
  catalog.yaml
  epics/library/
    epic.yaml
    plans/search/plan.yaml
    plans/search/taches/implementation.yaml
  plans/independent/plan.yaml
```

Add `definition.epics: [epics/library/epic.yaml]` to the catalog. Its WorkEpic document is:

```yaml
apiVersion: agentstration.io/v1
kind: WorkEpic
metadata:
  name: library
definition:
  id:
    value: 49ef965e-1fae-463a-8741-eef7e7217fd7
  displayName: Library improvements
  description: Optional logical grouping
  planIds:
    - value: 28cc0c95-249a-4412-8309-334247df0c3e
```

Membership is declared once in the epic, by plan identity. Folder placement alone never changes it.
Each plan belongs to at most one epic. An epic needs no GitHub issue and adds no dependency,
allocation, acceptance or execution semantics. Removing it ungroups its plans without deleting them.

Catalog omission or `epics: null` preserves existing grouping. An explicit `epics: []` clears it;
otherwise the reviewed list is the complete desired organization. A pure grouping import can use
`plans: []`. Grouping changes have their own revision, independent of Project/plan/task versions.
Matching normalized content is a no-op even with a new command ID; receipts remain replay-safe.

Organization APIs are under `/api/workspaces/{workspace}/projects/{projectId}/organization`:

| Route | Behavior |
| --- | --- |
| `GET` | Current organization definition and its own revision. |
| `PUT` | `{ id, expectedVersion, definition: { epics: [...] } }` explicitly replaces organization. |
| `POST /publication` | `{ id, expectedOrganizationVersion, expectedRecordVersion }` publishes the current definition through the protected sharing connector. |
| `POST /import` | `{ id, expectedVersion, expectedDigest }` explicitly adopts the retained remote organization after pulling; referenced plans must already be associated locally. |

Publish shared plans before an organization referring to them. The separate `Organization/epics`
record never modifies their digests, allocation or usage. An uncertain publication is recovered at
the returned ordinary sharing-operation location; do not create another publication ID to bypass
an uncertain effect. Local grouping edits and normal pulls never write GitHub hierarchy/dependencies.
See [ADR-0277](../decisions/0277-epics-organize-plans-without-execution-authority.md).
