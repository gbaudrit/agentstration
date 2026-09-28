# Architecture documentation migration inventory

This inventory is the ledger for progressively consolidating the Agentstration DAT under `docs/architecture/`. A source remains in place until its still-valid content has reached the stated canonical destination and its links have been validated.

The target paths below express the information architecture defined in [Architecture documentation conventions](conventions.md). They do not require empty directories or placeholder pages to be created before content is ready to move.

## Current architecture section

| Current source | Current role and observed overlap | Canonical destination | Planned reconciliation |
| --- | --- | --- | --- |
| `docs/architecture/overview.md` | Architecture landing page with a transitional structural Mermaid overview | `architecture/overview.md` plus `architecture/c4/` views | Keep as the DAT entry point; replace structural Mermaid after L1–L3 publication |
| `docs/architecture/system-context.md` | Narrative plus a context-like Mermaid diagram | `architecture/c4/system-context.md` | Preserve narrative; replace the competing structural diagram in #605 |
| `docs/architecture/containers.md` | Short executable inventory that predates the independent Console and current AEP topology | `architecture/c4/containers.md` and deployment pages | Reconcile against executables, composition roots, and ADRs in #606 |
| `docs/architecture/project-structure.md` | Physical solution inventory with transitional Management naming | `architecture/implementation/project-structure.md` | Reconcile project mapping without turning projects into C4 components |
| `docs/architecture/dependency-rules.md` | Logical dependency narrative plus a structural Mermaid graph | `architecture/implementation/dependency-rules.md` | Preserve enforced rules; link them to L3 components and architecture tests |
| `docs/architecture/resource-model.md` | Resource lifecycle narrative plus a structural flowchart | `architecture/data/resource-model.md` | Reconcile with family ownership and decide whether the diagram is structural or behavioral |
| `docs/architecture/persistence.md` | Store inventory containing removed legacy Content/Memory material | `architecture/data/persistence.md` | Reconcile SQLite/PostgreSQL logical boundaries and remove superseded claims only after comparison |
| `docs/architecture/runtime-execution.md` | Runtime narrative linking to the consolidated Agent and AEP/MCP scenarios | `architecture/dynamic-views.md` plus runtime narrative | Reconciled in #608; retain the focused narrative as a discoverable entry point |
| `docs/architecture/flow-execution.md` | Flow narrative linking to the consolidated Work, Flow, and Tool scenarios | `architecture/dynamic-views.md` plus Flow narrative | Reconciled in #608; retain the focused narrative as a discoverable entry point |
| `docs/architecture/standalone-mode.md` | Local-first deployment narrative | `architecture/deployment/standalone.md` | Reconcile direct, Aspire, and Compose profiles while preserving local defaults |
| `docs/architecture/multi-tenancy.md` | Cross-cutting isolation narrative with outdated coverage statements | `architecture/cross-cutting/tenancy-and-isolation.md` | Reconcile against current authorization and Workspace-scoping evidence |

## Root-level architecture sources

| Current source | Current role and observed overlap | Canonical destination | Planned reconciliation |
| --- | --- | --- | --- |
| `docs/architecture.md` | Detailed and comparatively current implementation inventory, flows, boundaries, and ADR catalog | Multiple DAT pages, primarily `implementation/`, `runtime/`, and `cross-cutting/` | Split progressively in #609; retain until every section is mapped and compared |
| `docs/management-plane.md` | Earlier Management Plane boundary and route summary | `architecture/c4/server-components.md` and resource-family/data pages | Reconcile terminology with the Control Plane umbrella and plural family ownership |
| `docs/runtime-plane.md` | Earlier Runtime boundary and capability summary | `architecture/c4/server-components.md` and `architecture/runtime/` | Preserve reconstructibility and provider boundaries; reconcile current capabilities |
| `docs/work-plane.md` | Work ownership, lifecycle, API, and earlier local limits | `architecture/c4/server-components.md`, `architecture/runtime/work-execution.md`, and reference pages | Separate stable architecture from API reference and superseded limitations |
| `docs/resource-model.md` | Concise resource-envelope description overlapping architecture and reference pages | `architecture/data/resource-model.md` with links to resource reference | Preserve invariants once; keep protocol details in Reference |
| `docs/domain-model.md` | Earlier product-domain narrative | `architecture/data/` or Concepts according to content | Classify each section before migration; do not duplicate conceptual guidance |
| `docs/flow.md` | Flow kinds, versioning, API, and runtime boundary | `architecture/runtime/` plus Flow concepts/reference | Separate behavioral architecture from user-facing and API material |
| `docs/standalone.md` | Detailed standalone implementation note | `architecture/deployment/standalone.md` | Merge with the current architecture page after checking launch profiles and stores |
| `docs/reconciliation.md` | Existing product/runtime reconciliation narrative | Relevant runtime or implementation page | Assess terminology and retain only current architecture behavior |

## Sources that retain separate ownership

| Source | Ownership after DAT migration |
| --- | --- |
| `docs/decisions/` | Remains the ADR history and decision source; DAT pages link to decisions rather than copying them |
| `docs/concepts/` | Remains user- and contributor-facing conceptual documentation |
| `docs/reference/` | Remains the precise product, API, configuration, and resource reference |
| `docs/getting-started/` | Remains task-oriented onboarding and launch guidance |
| `docs/contributing/` | Remains contributor workflow and governance documentation |

## Removal gate

A legacy page can be removed or reduced to a replacement pointer only when:

1. every still-valid section has a canonical destination;
2. obsolete statements have been checked against implementation and ADR evidence;
3. structural diagrams defer to a published LikeC4 view;
4. dynamic diagrams have been migrated or deliberately retained as Mermaid;
5. inbound links and Docusaurus navigation have been updated; and
6. the complete documentation build succeeds.
