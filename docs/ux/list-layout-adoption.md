# List-layout adoption on codex/ux-corrections

## Bounded ownership

This batch owns only Runtime Profiles (`/runtimeprofiles`) and Data Source Profiles (`/data-source-profiles`), their list adapters, necessary localization, affected existing regressions and this collection inventory. Other accessible work is migrating resource details and fixing CI formatting; no overlapping list ownership was found. This checkout is separate from those workspaces. Shared components, pilots, details and creation forms are outside this batch.

Each screen is completed, checked, committed and published before the next starts. The reusable scenario #1242 remains blocked pending explicit UX validation and merge. Ad hoc smoke checks do not implement that scenario. Existing parent #1241 and child #1242 track this composition work; no additional duplicate scenario issue is created.

## Current batch

| Screen | Data and query contract | Delivery and validation |
| --- | --- | --- |
| Runtime Profiles | Existing complete `ValueResponse` catalog. Local name search and runtime, session, streaming and additional namespace filters; exact filtered total, 20/50/100 local paging. No extra requests. | Template adopted; one column declaration per information value; exact resource links and scope/governance retained. Redundant Open-only menu removed. Creation permission-gated. Five component cases pass including EN/FR, complete-catalog filtering, paging, retained cards/results, shrink, stale responses, context replacement and denied access. Release component and Web builds pass without warnings. Three collection-catalog assertions pass. Deterministic Web/SQLite browser smoke passes at 1440×1000 in English and 390×844 in French using system Chromium: populated table/cards, paging, filter reset, no matches, refresh and no document horizontal overflow. Captures inspected locally under `/tmp/runtimeprofiles-*.png`. The bundled browser download was denied; system Chromium was used. Full functional suites and new reusable #1242 journey were not run. Incoming `e7ca713e` changes tool-editor formatting only; no new behavioral retest is justified. |
| Data Source Profiles | Existing catalog plus one authorized Extension inventory load. | Reserved for the next commit; not started. |

## Remaining top-level Console list adapters

This source inventory identifies concrete top-level list adapters still outside the new page composition. Embedded collections, Workplace lists and specialized operations remain governed by `collection-surfaces-1121.json` and require separate bounded review; the presence of `CollectionView` alone does not establish template adoption. Connected Services and Flows are the protected pilots.

| Source | Routes / aliases | Batch status |
| --- | --- | --- |
| `AgentRuns.razor` | `/agent-runs` | Outside this batch; page composition remaining |
| `Agents.razor` | `/agents` | Outside this batch; page composition remaining |
| `Artifacts.razor` | `/artifacts` | Outside this batch; page composition remaining |
| `BootstrapProfiles.razor` | `/settings/bootstrap` | Outside this batch; page composition remaining |
| `ConnectedServices.razor` | `/connected-services` | Protected pilot |
| `DataSourceProfiles.razor` | `/data-source-profiles` | Reserved next |
| `DataSources.razor` | `/data-sources` | Outside this batch; page composition remaining |
| `Deployments.razor` | `/agent-instances`, `/deployments` | Outside this batch; page composition remaining |
| `Extensions.razor` | `/extensions` | Outside this batch; page composition remaining |
| `Flows.razor` | `/flows` | Protected pilot |
| `KnowledgeSources.razor` | `/knowledge-sources` | Outside this batch; page composition remaining |
| `ModelProfiles.razor` | `/modelprofiles` | Outside this batch; page composition remaining |
| `ModelProviders.razor` | `/modelproviders` | Outside this batch; page composition remaining |
| `OrganizationAccess.razor` | `/settings/organization/access` | Outside this batch; page composition remaining |
| `OrganizationMembers.razor` | `/settings/organization/members` | Outside this batch; page composition remaining |
| `OrganizationSecurityAudit.razor` | `/settings/organization/security-audit` | Outside this batch; page composition remaining |
| `Packs.razor` | `/packs` | Outside this batch; page composition remaining |
| `Parameters.razor` | `/parameters` | Outside this batch; page composition remaining |
| `ResourcePlans.razor` | `/resource-plans` | Outside this batch; page composition remaining |
| `ResourceScopes.razor` | `/settings/resource-scopes` | Outside this batch; page composition remaining |
| `RuntimeProfiles.razor` | `/runtimeprofiles` | Adopted in this batch |
| `RuntimeWorkers.razor` | `/settings/runtime-workers` | Outside this batch; page composition remaining |
| `Schemas.razor` | `/schemas` | Outside this batch; page composition remaining |
| `Secrets.razor` | `/secrets` | Outside this batch; page composition remaining |
| `Settings.razor` | `/settings` | Outside this batch; page composition remaining |
| `SourceProviders.razor` | `/sourceproviders` | Outside this batch; page composition remaining |
| `SourceRegistries.razor` | `/settings/source-registries` | Outside this batch; page composition remaining |
| `Sources.razor` | `/settings/sources` | Outside this batch; page composition remaining |
| `StorageTypes.razor` | `/storage-types` | Outside this batch; page composition remaining |
| `Storages.razor` | `/storages` | Outside this batch; page composition remaining |
| `ToolCategories.razor` | `/tools/categories` | Outside this batch; page composition remaining |
| `ToolDefinitions.razor` | `/tools/definitions` | Outside this batch; page composition remaining |
| `ToolProviders.razor` | `/tools/providers` | Outside this batch; page composition remaining |
| `ToolSets.razor` | `/tools/sets` | Outside this batch; page composition remaining |
| `Tools.razor` | `/tools` | Outside this batch; page composition remaining |
| `TransformationFunctions.razor` | `/function-kinds`, `/transformation-functions` | Outside this batch; page composition remaining |
| `Triggers.razor` | `/triggers` | Outside this batch; page composition remaining |
| `Vaults.razor` | `/vaults` | Outside this batch; page composition remaining |
