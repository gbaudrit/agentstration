# Functional task trees

The Work API supports durable atomic and composite tasks independently of an Entry, Git repository, external issue tracker or model. A functional task retains the existing WorkTask/WorkItem identity. Its hierarchy is separate from Flow execution ancestry and Task continuations.

## Create a tree

Send `POST /api/workspaces/{workspaceName}/tasks/tree` with the run-submission permission in the current authorized Workspace. Every task has a caller-supplied stable UUID. Supply exactly one root and include every referenced parent in the request.

```json
{
  "rootTaskId": "10000000-0000-0000-0000-000000000001",
  "tasks": [
    {
      "id": "10000000-0000-0000-0000-000000000001",
      "kind": 1,
      "title": "Assessment",
      "instruction": "Assess the collected evidence"
    },
    {
      "id": "10000000-0000-0000-0000-000000000002",
      "kind": 0,
      "parentTaskId": "10000000-0000-0000-0000-000000000001",
      "title": "Collect",
      "instruction": "Collect evidence"
    }
  ]
}
```

`kind` is the invariant enum `Atomic = 0`, `Composite = 1`. Only composites can have children. Empty composites remain Pending; creation does not imply successful completion. Titles, instructions and descriptions are user-authored content.

Successful creation returns `201 Created`, a `Location` for the tree and the stored nodes with kind, parent, status, version and timestamps. Replaying the same tree identifiers and normalized content also returns `201` with the existing state, without resetting timestamps, creating duplicate tasks or requesting execution. Task order in the request does not affect replay. Changing the original intent under the same identity returns `409 task_tree_conflict`.

Creation is atomic: a collision on any task rolls back the whole new family. The request cannot attach an existing task or reference a parent outside the submitted tree. Caller ownership comes from the authenticated principal; no ownership override is accepted.

## Read a tree or subtree

Use `GET /api/workspaces/{workspaceName}/tasks/{taskId}/tree` with run-read permission. A root returns its family; a descendant returns only itself and its descendants. Reads are restricted to the current Workspace and principal. Missing or inaccessible roots return `404 work_task_not_found`.

Existing Entry/Trigger tasks can be read as atomic single-node trees. New task nodes also participate in existing task supervision; technical Flow-only WorkItems remain excluded from Workplace task classification.

## Bounds and failures

- At most 100 tasks and eight parent edges from the root.
- Titles are required and limited to 512 characters; instructions are required and limited to 100,000 characters; descriptions are limited to 2,000 characters.
- Aggregate title/instruction/description text is limited to 1,000,000 characters; HTTP creation bodies are limited to 8 MiB.
- Duplicate identifiers, cycles, multiple roots, unknown parents, invalid kinds and atomic parents return Problem Details with invariant validation codes and status `400`.
- Tree membership and kind are immutable. Existing single-Task deletion returns `409 task_tree_delete_not_supported` for a tree member, preserving its family.

## Persistence and current scope

Work-owned SQLite stores the hierarchy in the WorkItem snapshot and indexes the tree identity by Workspace and principal. Initialization adds the nullable projection/index to existing stores. PostgreSQL has the equivalent migration and adapter behavior; live PostgreSQL validation is opt-in.

Creation and reading are the #946 slice. Composite outcome consolidation (#947), WorkPlan dependencies/execution (#948/#949), shared-project synchronization and the Workplace authoring journey are subsequent capabilities. See [ADR-0256](../decisions/0256-functional-task-trees-extend-work-items.md).
