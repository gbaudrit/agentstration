# List-layout adoption on codex/ux-corrections

## Bounded ownership

The current continuation owns Tool Categories (`/tools/categories`) followed by Tool Providers (`/tools/providers`), their list adapters, necessary localization, affected existing regressions and this collection inventory. Runtime Profiles and Data Source Profiles are the completed initial batch. Other accessible work is migrating resource details and fixing CI formatting; no overlapping list ownership was found. This checkout is separate from those workspaces. Shared components, pilots, details and creation forms are outside this batch.

Each screen is completed, checked, committed and published before the next starts. The reusable scenario #1242 remains blocked pending explicit UX validation and merge. Ad hoc smoke checks do not implement that scenario. Existing parent #1241 and child #1242 track this composition work; no additional duplicate scenario issue is created.

## Current batch

| Screen | Data and query contract | Delivery and validation |
| --- | --- | --- |
| Runtime Profiles | Existing complete `ValueResponse` catalog. Local name search and runtime, session, streaming and additional namespace filters; exact filtered total, 20/50/100 local paging. No extra requests. | Template adopted; one column declaration per information value; exact resource links and scope/governance retained. Redundant Open-only menu removed. Creation permission-gated. Published as `6dd728fd`. Five component cases pass including EN/FR, complete-catalog filtering, paging, retained cards/results, shrink, stale responses, context replacement and denied access. Release component and Web builds pass without warnings. Three collection-catalog assertions pass. Deterministic Web/SQLite browser smoke passes at 1440×1000 in English and 390×844 in French using system Chromium: populated table/cards, paging, filter reset, no matches, refresh and no document horizontal overflow. Captures inspected locally under `/tmp/runtimeprofiles-*.png`. The bundled browser download was denied; system Chromium was used. Full functional suites and new reusable #1242 journey were not run. Incoming `e7ca713e` changes tool-editor formatting only; no new behavioral retest is justified. |
| Data Source Profiles | Existing catalog plus one authorized Extension inventory load. | Template adopted. Search and additional namespace filter cover the complete profile response with exact filtered totals and 20/50/100 local paging. Publication, exact scoped resource and Extension links, governance and distinct Extension/Bundle versions retained. Provenance remains informational because inventory enrichment is optional; no misleading Extension filter is exposed. Creation permission-gated; Open-only menu removed. Six new component cases plus two existing EN/FR provenance cases pass. Release component, Web and GovernedInventoryHost builds pass without warnings. Three catalog assertions and TypeScript compilation pass. Ad hoc deterministic GovernedInventoryHost/SQLite smoke passes at 1440×1000 in English and 390×844 in French using system Chromium, including populated table/cards, namespace filtering, no matches/reset, refresh, exact resource links, distinct versions and no document horizontal overflow. Captures inspected at `/tmp/data-source-profiles-*.png`. Existing complete browser journeys and full functional suites were not rerun; #1242 remains blocked. |
| Tool Categories | Existing complete `ValueResponse` catalog. Technical/display-name and description search, exact member-count column filter and additional namespace filter, exact totals and local 20/50/100 paging. | Published as `4d4ecc16`. Template adopted; existing legitimate Tool-family navigation and exact owner links retained with context. Redundant Manage-only column removed; authorized create action provided for header and empty state. Five EN/FR component cases pass: catalog filtering, page reset, retained cards, refresh failure/retry, shrink correction, stale/context-replaced responses and denied access. Release Web build and full targeted dotnet format pass. Three collection catalog assertions pass. Desktop/mobile deterministic executable smoke and source proof: [evidence](evidence/list-layout/tool-categories/README.md). Full suites and #1242 reusable journey remain deferred. |
| Tool Providers | Existing complete `ValueResponse` catalog; technical/display-name search, discovery status, provider type, enabled, advanced exact tool count and additional namespace filters; exact totals and local 20/50/100 paging. | Template adopted. Existing legitimate family destinations, exact resource links, discovery state and managed/inherited markers retained; redundant Manage-only column removed. Creation permission-gated. AEP/MCP type labels declared symmetrically in EN/FR resources. Six component cases pass, including EN/FR filtering/paging and view retention, failed refresh/retry/shrink, stale/context-replaced responses, permission gates and neutral unknown discovery states. Release Web build passes without warnings; targeted full dotnet format, three catalog assertions and EN/FR RESX symmetry checks pass. Deterministic desktop English/mobile French smoke verifies populated table/cards, search, paging, type/enabled/status filtering, reset and refresh. [Fresh captures and source proof](evidence/list-layout/tool-providers/README.md) record the actual fixture limits. Incoming `c545af27` modifies YAML tests only. Full suites and #1242 reusable journey remain deferred. |

## Remaining top-level Console list adapters

This source inventory identifies concrete top-level list adapters still outside the new page composition. Embedded collections, Workplace lists and specialized operations remain governed by `collection-surfaces-1121.json` and require separate bounded review; the presence of `CollectionView` alone does not establish template adoption. Connected Services and Flows are the protected pilots.

| Source | Routes / aliases | Batch status |
| --- | --- | --- |
| `AgentRuns.razor` | `/agent-runs` | Outside this batch; page composition remaining |
| `Agents.razor` | `/agents` | Outside this batch; page composition remaining |
| `Artifacts.razor` | `/artifacts` | Outside this batch; page composition remaining |
| `BootstrapProfiles.razor` | `/settings/bootstrap` | Outside this batch; page composition remaining |
| `ConnectedServices.razor` | `/connected-services` | Protected pilot |
| `DataSourceProfiles.razor` | `/data-source-profiles` | Adopted in this batch |
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
| `ToolCategories.razor` | `/tools/categories` | Adopted in this continuation |
| `ToolDefinitions.razor` | `/tools/definitions` | Outside this batch; page composition remaining |
| `ToolProviders.razor` | `/tools/providers` | Adopted in this continuation |
| `ToolSets.razor` | `/tools/sets` | Outside this batch; page composition remaining |
| `Tools.razor` | `/tools` | Outside this batch; page composition remaining |
| `TransformationFunctions.razor` | `/function-kinds`, `/transformation-functions` | Outside this batch; page composition remaining |
| `Triggers.razor` | `/triggers` | Outside this batch; page composition remaining |
| `Vaults.razor` | `/vaults` | Outside this batch; page composition remaining |
