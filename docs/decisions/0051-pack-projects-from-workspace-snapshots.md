# ADR-0051: Pack Projects can originate from reviewed workspace snapshots

Status: Accepted — 2026-08-17

## Context

Pack Projects initially originated only by forking an installed Pack. Authors also need to assemble a Pack from resources they already created in the current workspace. Copying only the resources explicitly checked in a screen would produce incomplete archives because Entries, Flows, Agents, model configuration, tools, and credentials form a dependency graph. Copying environment-specific configuration or Secret values would make the artifact unsafe and non-portable.

## Decision

A Pack Project has an explicit source kind. A fork retains its installed Pack origin; a workspace composition records the resource keys and archive paths of an immutable, server-generated source snapshot.

The application owns composition orchestration and depends on an `IPackWorkspaceResourceCatalog` port. The Infrastructure adapter reads each resource through its owning Management, Flow, or Work storage boundary, describes its dependencies, and exports a clean manifest. The Console uses the same service through three Management endpoints to list the inventory, preview a selection, and create the project.

Preview computes a transitive dependency closure. The first increment can include default-namespace `Agent`, `Flow`, and `Entry` resources. `ModelProfile` and `Secret` dependencies become logical installation bindings; their concrete resources and Secret values are not copied. A dependency that cannot yet be represented blocks project creation. The server recomputes this preview during creation and validates the generated archive with the normal Pack reader before storing it.

Creation does not mutate or take ownership of workspace resources. The generated archive is content-addressed and becomes the source of Pack Project revision 1. Subsequent workspace changes therefore cannot alter an existing revision implicitly.

## Consequences

- Authors can construct a Pack without first installing or downloading one.
- The review screen explains automatic inclusions, external bindings, and blocking dependencies before creating state.
- Installed copies still use the Pack identity namespace and cannot conflict with the original default-namespace resources.
- Tools, provider/runtime configuration, non-default source namespaces, refresh/rebase, and arbitrary cross-module dependencies remain explicit follow-up work rather than being copied unsafely.
