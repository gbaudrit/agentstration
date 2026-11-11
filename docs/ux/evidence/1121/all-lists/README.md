# Expanded real-application evidence (#1121)

Captured from the executable Console and Workplace with deterministic local services; these are application screenshots, not mockups. See the [adoption matrix](../../../all-list-adoption-1121.md) and [surface manifest](../../../collection-surfaces-1121.json).

## Campaign provenance

- Baseline route campaign: 45 distinct Console routes × four viewport/locale/theme variants = **180 distinct route checks**. The raw [campaign.json](campaign.json) includes four duplicate PAT checks and five populated source/Pack checkpoints. Route checks verified successful rendering, applicable responsive collection mode, complete clipboard values, menu keyboard/Escape/focus behavior where a menu existed, and document width. Empty collections remain empty-state evidence.
- The baseline ended with three stale-circuit JavaScript errors after rapid navigation. The collection observer now unsubscribes on detach/pagehide and only suppresses a pending callback after teardown. Five focused JavaScript tests cover observer teardown and late initialization of detached menus/disclosures.
- The final [delta campaign](delta-campaign.json), against `4347b6b`, passed **19 captures** plus 15 rapid route/resize checks with **zero unhandled page errors**. It replaces Artifact and PAT images, adds genuinely active Agent instances in all four variants and refreshes Workplace. Native PAT light/dark follows the emulated system color scheme.
- All resource/tenant identifiers are disposable local fixtures. No generated token secret is included. UX acceptance and both merges remain pending.

## Original five screens in the expanded campaign

| Screen | Desktop English dark | Mobile French dark |
| --- | --- | --- |
| `/agents` | [Table](agents-desktop-en-dark.png) | [Cards](agents-mobile-fr-dark.png) |
| `/modelprofiles` | [Table](modelprofiles-desktop-en-dark.png) | [Cards](modelprofiles-mobile-fr-dark.png) |
| `/data-source-profiles` | [Table](data-source-profiles-desktop-en-dark.png) | [Cards](data-source-profiles-mobile-fr-dark.png) |
| `/runtimeprofiles` | [Table](runtimeprofiles-desktop-en-dark.png) | [Cards](runtimeprofiles-mobile-fr-dark.png) |
| `/flows` | [Table](flows-desktop-en-dark.png) | [Cards](flows-mobile-fr-dark.png) |

## All primary Console routes

Light screenshots are provided for representative resource, operational, administrative and account surfaces. The machine records include all four mode checks even where an additional light image was not retained.

| Route | Desktop EN dark | Mobile FR dark | Desktop EN light | Mobile FR light |
| --- | --- | --- | --- | --- |
| `/account/pat` | [View](account-pat-desktop-en-dark.png) | [View](account-pat-mobile-fr-dark.png) | [View](account-pat-desktop-en-light.png) | [View](account-pat-mobile-fr-light.png) |
| `/agent-instances` | [View](agent-instances-desktop-en-dark.png) | [View](agent-instances-mobile-fr-dark.png) | — | — |
| `/agent-runs` | [View](agent-runs-desktop-en-dark.png) | [View](agent-runs-mobile-fr-dark.png) | — | — |
| `/agents` | [View](agents-desktop-en-dark.png) | [View](agents-mobile-fr-dark.png) | [View](agents-desktop-en-light.png) | [View](agents-mobile-fr-light.png) |
| `/artifacts` | [View](artifacts-desktop-en-dark.png) | [View](artifacts-mobile-fr-dark.png) | [View](artifacts-desktop-en-light.png) | [View](artifacts-mobile-fr-light.png) |
| `/cleanup` | [View](cleanup-desktop-en-dark.png) | [View](cleanup-mobile-fr-dark.png) | — | — |
| `/conversations` | [View](conversations-desktop-en-dark.png) | [View](conversations-mobile-fr-dark.png) | — | — |
| `/data-source-profiles` | [View](data-source-profiles-desktop-en-dark.png) | [View](data-source-profiles-mobile-fr-dark.png) | — | — |
| `/data-sources` | [View](data-sources-desktop-en-dark.png) | [View](data-sources-mobile-fr-dark.png) | — | — |
| `/entries` | [View](entries-desktop-en-dark.png) | [View](entries-mobile-fr-dark.png) | [View](entries-desktop-en-light.png) | [View](entries-mobile-fr-light.png) |
| `/extensions` | [View](extensions-desktop-en-dark.png) | [View](extensions-mobile-fr-dark.png) | — | — |
| `/flow-runs` | [View](flow-runs-desktop-en-dark.png) | [View](flow-runs-mobile-fr-dark.png) | — | — |
| `/flows` | [View](flows-desktop-en-dark.png) | [View](flows-mobile-fr-dark.png) | — | — |
| `/knowledge-sources` | [View](knowledge-sources-desktop-en-dark.png) | [View](knowledge-sources-mobile-fr-dark.png) | — | — |
| `/modelprofiles` | [View](modelprofiles-desktop-en-dark.png) | [View](modelprofiles-mobile-fr-dark.png) | — | — |
| `/modelproviders` | [View](modelproviders-desktop-en-dark.png) | [View](modelproviders-mobile-fr-dark.png) | — | — |
| `/packs` | [View](packs-desktop-en-dark.png) | [View](packs-mobile-fr-dark.png) | — | — |
| `/parameters` | [View](parameters-desktop-en-dark.png) | [View](parameters-mobile-fr-dark.png) | — | — |
| `/resource-plans` | [View](resource-plans-desktop-en-dark.png) | [View](resource-plans-mobile-fr-dark.png) | — | — |
| `/run-events` | [View](run-events-desktop-en-dark.png) | [View](run-events-mobile-fr-dark.png) | — | — |
| `/runtimeprofiles` | [View](runtimeprofiles-desktop-en-dark.png) | [View](runtimeprofiles-mobile-fr-dark.png) | — | — |
| `/schemas` | [View](schemas-desktop-en-dark.png) | [View](schemas-mobile-fr-dark.png) | — | — |
| `/secrets` | [View](secrets-desktop-en-dark.png) | [View](secrets-mobile-fr-dark.png) | — | — |
| `/settings` | [View](settings-desktop-en-dark.png) | [View](settings-mobile-fr-dark.png) | — | — |
| `/settings/bootstrap` | [View](settings-bootstrap-desktop-en-dark.png) | [View](settings-bootstrap-mobile-fr-dark.png) | — | — |
| `/settings/organization` | [View](settings-organization-desktop-en-dark.png) | [View](settings-organization-mobile-fr-dark.png) | — | — |
| `/settings/organization/access` | [View](settings-organization-access-desktop-en-dark.png) | [View](settings-organization-access-mobile-fr-dark.png) | — | — |
| `/settings/organization/members` | [View](settings-organization-members-desktop-en-dark.png) | [View](settings-organization-members-mobile-fr-dark.png) | [View](settings-organization-members-desktop-en-light.png) | [View](settings-organization-members-mobile-fr-light.png) |
| `/settings/organization/security-audit` | [View](settings-organization-security-audit-desktop-en-dark.png) | [View](settings-organization-security-audit-mobile-fr-dark.png) | — | — |
| `/settings/organization/structure` | [View](settings-organization-structure-desktop-en-dark.png) | [View](settings-organization-structure-mobile-fr-dark.png) | — | — |
| `/settings/resource-scopes` | [View](settings-resource-scopes-desktop-en-dark.png) | [View](settings-resource-scopes-mobile-fr-dark.png) | — | — |
| `/settings/runtime-workers` | [View](settings-runtime-workers-desktop-en-dark.png) | [View](settings-runtime-workers-mobile-fr-dark.png) | [View](settings-runtime-workers-desktop-en-light.png) | [View](settings-runtime-workers-mobile-fr-light.png) |
| `/settings/source-registries` | [View](settings-source-registries-desktop-en-dark.png) | [View](settings-source-registries-mobile-fr-dark.png) | — | — |
| `/settings/source-registries/discovery` | [View](settings-source-registries-discovery-desktop-en-dark.png) | [View](settings-source-registries-discovery-mobile-fr-dark.png) | — | — |
| `/settings/sources` | [View](settings-sources-desktop-en-dark.png) | [View](settings-sources-mobile-fr-dark.png) | — | — |
| `/sourceproviders` | [View](sourceproviders-desktop-en-dark.png) | [View](sourceproviders-mobile-fr-dark.png) | — | — |
| `/tasks` | [View](tasks-desktop-en-dark.png) | [View](tasks-mobile-fr-dark.png) | — | — |
| `/tools` | [View](tools-desktop-en-dark.png) | [View](tools-mobile-fr-dark.png) | [View](tools-desktop-en-light.png) | [View](tools-mobile-fr-light.png) |
| `/tools/categories` | [View](tools-categories-desktop-en-dark.png) | [View](tools-categories-mobile-fr-dark.png) | — | — |
| `/tools/definitions` | [View](tools-definitions-desktop-en-dark.png) | [View](tools-definitions-mobile-fr-dark.png) | — | — |
| `/tools/providers` | [View](tools-providers-desktop-en-dark.png) | [View](tools-providers-mobile-fr-dark.png) | — | — |
| `/tools/sets` | [View](tools-sets-desktop-en-dark.png) | [View](tools-sets-mobile-fr-dark.png) | — | — |
| `/triggers` | [View](triggers-desktop-en-dark.png) | [View](triggers-mobile-fr-dark.png) | — | — |
| `/vaults` | [View](vaults-desktop-en-dark.png) | [View](vaults-mobile-fr-dark.png) | — | — |
| `/workspaces` | [View](workspaces-desktop-en-dark.png) | [View](workspaces-mobile-fr-dark.png) | — | — |

## Populated operations and embedded collections

| Evidence | Captures |
| --- | --- |
| `source-lifecycle-acquisitions` | [View](source-lifecycle-acquisitions-desktop-en-dark.png) |
| `source-lifecycle-projections` | [View](source-lifecycle-projections-desktop-en-dark.png) |
| `pack-project-composer` | [View](pack-project-composer-desktop-en-dark.png) |
| `pack-project-created` | [View](pack-project-created-desktop-en-dark.png) |
| `pack-project-built` | [View](pack-project-built-desktop-en-dark.png) |
| `agent-instances-active` | [desktop-en-dark](agent-instances-active-desktop-en-dark.png), [desktop-en-light](agent-instances-active-desktop-en-light.png), [mobile-fr-dark](agent-instances-active-mobile-fr-dark.png), [mobile-fr-light](agent-instances-active-mobile-fr-light.png) |
| `artifacts-staged` | [desktop-en-dark](artifacts-staged-desktop-en-dark.png), [desktop-en-light](artifacts-staged-desktop-en-light.png), [mobile-fr-dark](artifacts-staged-mobile-fr-dark.png), [mobile-fr-light](artifacts-staged-mobile-fr-light.png) |
| Workplace `/w/default/d/home` | [View](workplace-home-mobile-fr-light.png) |
| Workplace `/w/default/tasks` | [View](workplace-tasks-mobile-fr-light.png) |
| Workplace `/w/default/notifications` | [View](workplace-notifications-mobile-fr-light.png) |

The populated browser workflows and existing domain regressions additionally verify Source acquisition/projection histories, immutable revisions, ToolSet members, provider models/capabilities, Trigger occurrences, Worker assignments, resource-access grants, organization membership and personal-token commands. Their traces remain test-run/CI artifacts; a screenshot of a host page alone does not certify every embedded interaction.
