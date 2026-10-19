# Architecture documentation migration inventory

This inventory records the consolidation of the Agentstration DAT under `docs/architecture/`. Former published paths remain as replacement pointers; canonical content is maintained only at the destinations below.

## Former flat architecture pages

| Compatibility path | Canonical destination | Reconciliation status |
| --- | --- | --- |
| `docs/architecture/system-context.md` | `architecture/c4/system-context.md` | Migrated; the shared LikeC4 model owns C4 structure |
| `docs/architecture/containers.md` | `architecture/c4/containers.md` | Migrated after executable, store, and AEP reconciliation |
| `docs/architecture/components.md` | `architecture/c4/server-components.md` | Migrated with responsibility-to-project mapping |
| `docs/architecture/dynamic-views.md` | `architecture/runtime/dynamic-views.md` | Migrated with consolidated Mermaid scenarios |
| `docs/architecture/runtime-execution.md` | `architecture/runtime/agent-execution.md` | Migrated; scenarios remain in the dynamic-view catalog |
| `docs/architecture/flow-execution.md` | `architecture/runtime/flow-execution.md` | Migrated; scenarios remain in the dynamic-view catalog |
| `docs/architecture/project-structure.md` | `architecture/implementation/project-structure.md` | Migrated without treating projects as C4 components |
| `docs/architecture/dependency-rules.md` | `architecture/implementation/dependency-rules.md` | Migrated; rules remain narrative and test-backed |
| `docs/architecture/resource-model.md` | `architecture/data/resource-model.md` | Migrated; competing structural flowchart removed |
| `docs/architecture/persistence.md` | `architecture/data/persistence.md` | Migrated and reconciled against the executable storage composition in #611 |
| `docs/architecture/standalone-mode.md` | `architecture/deployment/standalone.md` | Migrated with direct, Aspire, and Compose distinctions |
| `docs/architecture/multi-tenancy.md` | `architecture/cross-cutting/tenancy-and-isolation.md` | Migrated and reconciled against current authorization surfaces in #611 |

`docs/architecture/overview.md`, `conventions.md`, `interactive.mdx`, and this inventory remain canonical entry-point pages.

## Former root-level architecture pages

| Compatibility path | Canonical destination | Reconciliation status |
| --- | --- | --- |
| `docs/architecture.md` | `architecture/implementation/current-state.md` plus focused DAT pages | Detailed inventory moved intact; focused pages link to it rather than copying it |
| `docs/management-plane.md` | C4 server components, data model, and current-state inventory | Stable ownership retained; route inventory is implementation/reference evidence |
| `docs/runtime-plane.md` | Agent execution and runtime reconciliation | Reconstructibility and provider boundaries retained |
| `docs/work-plane.md` | Work execution | Stable ownership and lifecycle retained; obsolete local limitations removed |
| `docs/resource-model.md` | Data resource model and Resource reference | Envelope and immutability invariants consolidated once |
| `docs/domain-model.md` | Data model, runtime pages, and Concepts | Durable resource/runtime separation retained without duplicating product concepts |
| `docs/flow.md` | Flow execution, dynamic views, and Flow concepts | Architecture separated from API and authoring guidance |
| `docs/standalone.md` | Standalone deployment | Superseded seed-data and storage claims removed |
| `docs/reconciliation.md` | Runtime reconciliation | Current reconciliation responsibility retained |

## Sources with separate ownership

| Source | Ownership after DAT migration |
| --- | --- |
| `docs/decisions/` | ADR history and decision source; DAT pages link to decisions rather than copying them |
| `docs/concepts/` | User- and contributor-facing conceptual documentation |
| `docs/reference/` | Precise product, API, configuration, and resource reference |
| `docs/getting-started/` | Task-oriented onboarding and launch guidance |
| `docs/contributing/` | Contributor workflow and governance documentation |

## Removal gate

A compatibility page may be removed only when:

1. every still-valid section has a canonical destination;
2. obsolete statements have been checked against implementation and ADR evidence;
3. structural diagrams defer to the published LikeC4 view;
4. dynamic diagrams have been migrated or deliberately retained as Mermaid;
5. inbound links and Docusaurus navigation have been updated; and
6. the complete documentation build succeeds.
