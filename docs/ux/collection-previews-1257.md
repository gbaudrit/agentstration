# Resource collection Actions, saved Summaries and Preview

Implementation of [#1257](https://github.com/gbaudrit/agentstration/issues/1257), starting at main `51969396`. The [adoption/exception matrix](collection-previews-1257.json) retains every frame and specialized surface from `collection-surfaces-1121.json`, including embedded and operational lists. The checked matrix contains 42 adopted lists and five justified non-resource lists, retaining 395 assessed surface IDs (394 active renderers and one explicit historical alias). Each list is delivered in its own commit; existing Agents/Flows/Extensions implementations are reviewed as baselines.

## Ownership and navigation

`CollectionActions` and `ResourceCollectionPreview` own menu and modal/history presentation. Native hashchange/popstate is observed as well as Blazor navigation, so a closed Preview can reopen through the same fragment without a reload. The observer changes presentation only and never pushes/replaces navigation or bypasses dirty-form guards. `ResourceSummary` supplies the shared spacing and wrapping two-column saved-configuration layout in both full details and Preview. Each screen supplies its own authorized row actions and exact resource address. A family Preview owns the bounded scoped snapshot read, rejects returned owner/namespace/kind/name mismatches, and cancels/rejects superseded requests. It never scans collection pages or loads editor pickers. Summary receives saved values and independently supplied quick actions; it owns no reads or mutations.

Names and Open select `#overview`; Edit definition selects the existing definition form. `#preview/{kind}/{encoded-owner}/{namespace}/{name}` preserves the current collection query and supports direct/off-window targets. Open/Edit leave that history entry available for browser Back. Explicit close replaces the fragment; context replacement closes the old Preview. Initial authentication/loading is not a context replacement: the exact incoming anchor remains dormant until authorization is established. Native Preview hash pushes use public Blazor navigation before history commits, preserving navigation guards and history indices. Accepted same-document entry changes are observed alongside legacy fragment events; guarded traversals remain owned by native navigation. Existing return-to-list links remain explicit collection links and dirty-form guards remain in place.

Summaries reuse the template's Identity fragment in full details and render a complete copyable standalone Identity in Preview, including labels and applicable Extension provenance. Identity and Configuration initially open in Preview and remain independently foldable without unmounting child content or refetching. Preview hides the Configuration view selector while preserving automatic narrow-screen cards.

## Parameters

The list adds Open, authorized Edit definition and Preview. There is no row Delete command to duplicate the full editor's usage/ETag workflow. The shared saved Summary shows value type and readable value; it never resolves secrets or performs usage/picker reads. The endpoint only supports the default namespace, so other namespaces are rejected before any read. Exact owner is mandatory. The overview retains its existing usage metric and saved/draft lifecycle.

Validation: Console component/test project Release build passed with zero warnings/errors; 41 Parameter component/model/collection/resource-screen/Preview tests passed. All four desktop/mobile English/French light/dark browser variants passed; [observations](evidence/1257/parameters/README.md). Final offline results are recorded below. New reusable browser journeys remain deferred in native child [#1258](https://github.com/gbaudrit/agentstration/issues/1258) until the required maintainer UX validation and merge. Neither issue is closed by these implementation increments.

## Schema

Saved format, dialect, schema content and Source evidence. No formats catalog or version-history read in Preview. The family Preview uses `ISchemasClient.GetSchemaAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed with zero warnings/errors; 91 Schema tests passed, including exact-address Preview, bilingual disclosure and existing editor/collection regressions. All four desktop/mobile English/French light/dark browser variants passed; [observations](evidence/1257/schemas/README.md). Final offline results are recorded below.

## Vault

Saved Vault identity, provider type and descendant-use policy; initialization and key operations stay on the full detail page. The family Preview uses `ISecretsClient.GetVaultAsync(name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 43 Vault tests passed. All four desktop/mobile English/French light/dark browser variants passed; [observations](evidence/1257/vaults/README.md). Final offline results are recorded below.

## Secret

Saved Secret metadata, Vault reference, key, localized type and descendant-use policy. Secret contents are never read or displayed; value operations and usages stay on the full detail page. The family Preview uses `ISecretsClient.GetSecretAsync(name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 40 Secret tests passed. All four desktop/mobile English/French light/dark browser variants passed; [observations](evidence/1257/secrets/README.md). Final offline results are recorded below.

## ConnectedService

Saved identity, service type/version, literal options and Parameter/Secret references; no resolved secret values, catalog/discovery/usages fetch or editor draft. Reconcile and deactivation retain the full detail confirmation/ETag workflow. The family Preview uses `IConnectedServicesClient.GetConnectedServiceAsync(name, owner, namespace, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 56 Connected Service tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/connected-services/README.md). Final offline results are recorded below.

## StorageType

Saved identity, enabled flag, preferred immutable version, observed generic-use decision and access policy. Full details retain usages and version-contract panels without fetching usages/history for Preview. The independently supplied Preview quick actions add the existing version publication form. The family Preview uses `IStorageTypesClient.GetTypeAsync(exact address, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 54 storage types tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/storage-types/README.md). Final offline results are recorded below.

## Storage

Saved identity, pinned type/version, configuration/envelope, collections, access policy and declared Connected Service/export mappings. Preview never loads the type catalog, usages, diagnostics or document contents. The full overview retains its resolved type display name. The family Preview uses `IStoragesClient.GetAsync(exact address, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 140 storages tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/storages/README.md). Final offline results are recorded below.

## DataSourceProfile

Saved identity, actual known publication version, acquisition/discovery Flow references, configuration schema, tool bindings, limits and policies. Preview does not fetch revisions, Flow catalogs or pickers. Inherited row links retain the current administrative context. The family Preview uses `IDataSourceProfilesClient.GetAsync(namespace, name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 38 data source profiles tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/data-source-profiles/README.md). Final offline results are recorded below.

## RuntimeProfile

Saved identity, runtime type, execution/session/tool/streaming defaults and runtime options. Preview reads one exact snapshot without usages or editor pickers. Row and full-detail URLs retain exact owner/namespace and the current administrative context. The existing service exact-read capability is now exposed by the client and HTTP GET, with no unscoped fallback. The family Preview uses `IRuntimeProfilesClient.GetRuntimeProfileAsync(namespace, name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 31 runtime profiles tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/runtimeprofiles/README.md). Final offline results are recorded below.

Runtime Profile HTTP GET now accepts the existing exact `scopeRef` plus `resourceNamespace` address. It preserves the Instance-only scope policy and legacy requests without an explicit owner. Focused API integration passed: exact namespace homonyms and ETags, hidden unshared Instance resources from a Workspace context, and missing/unsupported exact targets without fallback.

## ToolProvider

Saved identity, localized provider type, enabled state, MCP transport/endpoint or command/argument/environment references and AEP Extension reference. Full details retain the existing ownership banner, discovery, tests, toggles and discovered Tools. Preview does not probe a provider or enumerate Tools. The family Preview uses `IToolsClient.GetProviderAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 34 tool providers tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/tools/providers/README.md). Final offline results are recorded below.

## Tool

Saved identity/configuration, capability, input/output schema summaries, observed discovery facts and known governance/provider references. Provider/Extension observations are optional saved caller inputs on the full overview, never loaded by Preview. Tools have a read-only inspection definition: Edit definition is not applicable. Preview independently offers the existing execution form only for enabled, available Tools with RunsExecute; it never invokes a Tool. The family Preview uses `IToolsClient.GetToolAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 35 tools tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/tools/README.md). Final offline results are recorded below.

## ToolSet

Saved identity, known published version, draft version/publication request, category, exact declared members and annotations. Tool Sets expose an existing read-only definition inspection, so Edit definition is not applicable. Preview independently links to the existing publication history without reading version lists until navigation; deletion remains in the full detail. The family Preview uses `IToolSetsClient.GetToolSetAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 37 tool sets tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/tools/sets/README.md). Final offline results are recorded below.

## ToolCategory

Saved identity and declared Tool membership references, independent of the editor selection/draft. Preview adds one exact Workspace/namespace/name read through the existing store; it never loads resolved members, Tools or Provider pickers. Full editor reads now use the exact owner when supplied. The family Preview uses `IToolCategoriesClient.GetCategoryAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 30 tool categories tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/tools/categories/README.md). Final offline results are recorded below.

## ToolDefinition

Saved identity and the existing Flow, Relay or Acquisition configuration. Both full detail adapters reuse the same saved Summary. Preview reads no Flow contracts, source catalogs, version history, usages or pickers; a Relay source generation is displayed as a catalog revision unless an actual known version is supplied. The family Preview uses `IToolDefinitionsClient.GetAsync(owner, name, namespace, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 95 tool definitions tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/tools/definitions/README.md). Final offline results are recorded below.

## ModelProvider

Saved identity, Extension/contribution reference, value bindings and specification overrides. Preview reads one exact Instance/namespace/name snapshot, without testing providers, discovering models or loading Extension/Parameter/Secret pickers. ModelProvider remains Instance-only under its existing scope policy. The family Preview uses `IModelProvidersClient.GetModelProviderAsync(namespace, name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 123 model providers tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/modelproviders/README.md). Final offline results are recorded below.

## ModelProfile

Saved identity, Provider/Model reference, generation/reasoning/output options, stop sequences and provider options. The full overview may supply an already known Provider owner; Preview does not infer one from another catalog. Exact Preview avoids model resolution, Extension/Provider/Model pickers, capabilities and usage reads. Existing row deletion retains its authorization, ETag and confirmation workflow. The family Preview uses `IModelProfilesClient.GetModelProfileAsync(namespace, name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 132 model profiles tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/modelprofiles/README.md). Final offline results are recorded below.

## DataSource

Saved identity and Profile-based or Dataset acquisition configuration, shared by both detail adapters. Optional already resolved service/operation/storage display names and observations stay in the full overview. Preview uses one exact snapshot and does not read readiness, operation contracts, service/storage catalogs, acquisitions, discovery or document contents. The family Preview uses `IDataSourcesClient.GetAsync(namespace, name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 137 data sources tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/data-sources/README.md). Final offline results are recorded below.

## KnowledgeSource

Saved identity, exact declared Flow references, projection parameters, input bindings, output storage pin and lifecycle/composition policy. Both full details and the standalone edit adapter reuse the saved Summary. Preview reads no readiness, resolved Flows, snapshots, Tool exposure, data-source/Storage catalogs or document content. Full-detail observations remain supplied by the owning page. The family Preview uses `IKnowledgeSourcesClient.GetAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 107 knowledge sources tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/knowledge-sources/README.md). Final offline results are recorded below.

## SourceProvider

Saved identity and declared Extension/contribution reference. Existing exact owner/namespace/name reads are reused; legacy client implementations now reject unsupported exact reads instead of silently ignoring the owner. Preview does not inspect Extension connections, enumerate contributions, probe the provider or read usages. Catalog Platform administrator authorization remains enforced by the existing endpoint. The family Preview uses `ISourceProvidersClient.GetSourceProviderAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 34 source providers tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/sourceproviders/README.md). Final offline results are recorded below.

## SourceRegistryRegistration

Saved Instance/default-namespace registration identity, endpoint and credential reference, trust, endpoint, refresh and cache policies. The new exact desired-state endpoint reads only the selected registration; no observed-state provisioning, refresh, index download, discovery cache or history read. Existing full-detail observation panels and row toggle/refresh commands are retained. Other namespaces and unsupported owners never fall back. The family Preview uses `ISourceRegistriesClient.GetRegistrationAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 40 source registries tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/settings/source-registries/README.md). Final offline results are recorded below.

## Source

Separate collection/detail route adapters preserve disposal and Preview history for the shared Sources screen. Saved Source identity, publisher, origin, local bindings and refresh policies. Full details additionally supply the already selected immutable publication and its declared channels. The exact Source Summary endpoint reads only Source and local SourceConfiguration by owner/publisher/name; it does not enumerate versions, providers, catalog materializations or channels. Preview omits unknown publication facts instead of resolving history. Existing full-detail verification/import observations remain caller-supplied. The family Preview uses `ISourceConsoleApiClient.GetSavedSummaryAsync(owner, namespace/publisher, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 98 sources tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/settings/sources/README.md). Final offline results are recorded below.

## Trigger

Saved Trigger identity, schedule, target, resolution and execution policy/input. Preview displays the logical administrative context, retaining the physical operational Workspace only in its exact address. The server rejects a requested physical owner different from the current operational context; no scope fallback, firing, schedule preview, history or Flow catalog read. Full details retain their existing failure observation and execution/history panels. The family Preview uses `ITriggerApiClient.GetAsync(exact physical owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 71 triggers tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/triggers/README.md). Final offline results are recorded below.

## TransformationFunction

Saved identity, enabled state and immutable version-to-kind/configuration references. Full details may supply already loaded native contract observations; exact Preview does not load implementations, Function Kind catalogs, editor options or mutation state. The family Preview uses `ITransformationFunctionsClient.GetAsync(exact FunctionReference, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 34 transformation functions tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/transformation-functions/README.md). Final offline results are recorded below.

## FunctionKind

The separate Function Kind catalog retains its existing immutable contract expansion and guarded activation command inside the row menu, and adds Open/Preview. A read-only addressable detail composes the same saved Summary through OverviewSummary. Preview performs only the exact kind read and never loads implementation choices or mutates activation. The native version/executor/schema dataset remains explicitly inventoried.

Validation: Release component/test build passed without warnings/errors; 48 function kinds tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/function-kinds/README.md). Final offline results are recorded below.

## InstalledPack

Saved installation identity/version, destination namespace, purpose/audience, source/installation facts, persisted Source provenance and declared binding/managed-resource records. The existing native management panel and new read-only details reuse the same saved Summary. Preview reads exactly one canonical Pack address through the existing scoped endpoint; malformed/non-default-namespace addresses never scan or fall back. Installation management remains a separate supplied navigation action for authorized owned installations, with its existing dialogs/ETags. The family Preview uses `IPacksClient.GetPackAtAddressAsync(exact address, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 70 installed packs tests passed. All applicable browser variants passed; see the final evidence index below.

## PackProject

The embedded project catalog gets independent Open, authorized Edit and Preview actions. Both overview and Preview share the native saved metadata and source provenance. Exact reads use the Workspace owner, default namespace and persisted GUID N name, retain the independent server-assigned UID for the full-detail route, and do not enumerate projects, archives, source documents or builds. Existing editing/build/installation guards remain in the full view.

Validation: Release component/test build passed without warnings/errors; 82 pack projects tests passed. All applicable browser variants passed; see the final evidence index below.

## RuntimeWorker

Workers use their existing Instance/default AwpWorkerIdentity enrollment coordinates (WorkerId N), with native GUID detail URLs. Row and quick actions offer inspection; there is no business definition form. The pure Summary shares native identity, compatibility and capacity facts with full details, and omits unavailable UID/labels/publication facts. The lazy endpoint reads one enrollment, one in-memory registration and a WorkerId-filtered bounded active-assignment count; it never scans worker catalogs or loads recent assignment history. PlatformAdmin authorization remains on the API.

Validation: Release component/test build passed without warnings/errors; 29 runtime workers tests passed. All applicable browser variants passed; see the final evidence index below.

## Entry

The native Entries card catalog gets Open, owned/unmanaged Edit and lazy Preview. Overview and Preview reuse saved draft configuration and the current persisted publication, with native Workspace/EntryId coordinates. The endpoint rejects a different execution Workspace before reading, and loads only the draft and current publication, without pickers, usage, validation, publication or Pack-catalog scans. Logical scope replaces internal operational Workspace presentation. Preview Edit requires the loaded catalog's known non-managed flag; a direct link outside that loaded catalog offers Open until the full editor resolves governance. No unknown management state is treated as editable. No UID or labels are invented for this native Work resource.

Validation: Release component/test build passed without warnings/errors; 39 entries tests passed. All applicable browser variants passed; see the final evidence index below.

## Model

Persisted Models embedded in the Provider catalog get Open and Preview alongside the existing guarded Create profile link. Discovery choices without ResourceName remain value choices, without fabricated addresses. Models are immutable observed resources: no Edit definition is invented. The shared native Summary renders provider identity, capabilities and limits from the saved snapshot. Full details may supply already-known saved override/effective facts and its existing override action; Preview never fetches the Provider, discovery/history or override editor. The new exact Instance/namespace/name GET preserves the full detail's provider UID/ETag override workflow.

Validation: Release component/test build passed without warnings/errors; 130 models tests passed. All applicable browser variants passed; see the final evidence index below.

## Agent (baseline completion)

The namespaced Pack read-only adapter now supplies its template Identity to the same saved Agent Summary used by the editor and Preview. Its native forms, Pack provenance, original source, model/runtime diagnostics and prepare-runtime commands remain in place; bootstrap namespaced resources keep their existing editor redirect. Row/Preview Edit follows that real redirect for owned unmanaged namespaced resources. Saved summaries retain complete Tool/ToolSet owners/members, behaviors and both schema bindings and inline contracts. References with unknown owners stay text unless full details supply a known exact destination.

Validation: Release component/test build passed without warnings/errors; 74 agents tests passed. All applicable browser variants passed; see the final evidence index below.

## Flow (baseline verification)

The existing exact-address Summary/Preview retains saved topology, current publication version and independent Designer navigation. No history read supplies an unknown publication date. Row ownership now also follows the explicit current context, and permission/context notifications clear or cancel a mounted adapter independently of its list shell. Read-only presentation follows actual editing availability.

Validation: Release component/test build passed without warnings/errors; 91 flows tests passed. All applicable browser variants passed; see the final evidence index below.

## Extension (baseline completion)

Manual/managed registrations keep independent native actions and their exact saved Preview. Standalone identity now includes actual read-only/governance and Extension provenance. Unregistered AEP enrollment rows are inspectable resources too: a second adapter reads their existing Instance/default/InstanceId N address and reuses the same pure Summary as full details. The endpoint preserves existing enrollment authorization, target visibility and sanitization; it never reads credentials, probes endpoints or issues pairing/rotation commands. Enrollments have no invented definition form.

Validation: Release component/test build passed without warnings/errors; 118 extensions tests passed. All applicable browser variants passed; see the final evidence index below.

## ResourcePlan

Resource Plans are inspectable native review resources. Their collection now offers Open and lazy Preview, reusing full review's saved identity/goal/schema/status/revision facts. The URL envelope carries the execution Workspace and PlanId N; the API validates that Workspace before the existing Tenant/Workspace-scoped GET. No Core UID/labels/namespace or definition form is fabricated. Review bindings, changes, activities, validation/materialization/application and their existing draft guards stay in the full review. Internal operational Workspace presentation uses the logical administrative scope.

Validation: Release component/test build passed without warnings/errors; 36 resource plans tests passed. All applicable browser variants passed; see the final evidence index below.

## Tool Provider embedded Tools

The Provider Tools panel reuses the same saved Tool Summary and exact adapter as the standalone Tools catalog. Open selects overview; Preview is lazy and has no definition Edit. The existing activation toggle, availability/governance guards and fresh ETag path stay independent. Changing Provider identity or losing access clears the panel.

Validation: Release component/test build passed without warnings/errors; 27 Provider Tools and Tool Preview tests passed. All applicable browser variants passed; see the final evidence index below.

## ConnectedServiceOperation

The embedded discovered-operation catalog now offers Open and lazy exact Preview. A read-only inspection overview shares saved identity/service/type pins, definition/digest and known manifest/time facts with Preview. No definition form or execution command is invented. The existing activation predicates and fresh ETag workflow remain in the parent panel; the existing schema-candidate authoring disclosure stays outside the menu. Reads use the existing exact operation GET without Service/type/discovery/Flow/history requests.

Validation: Release component/test build passed without warnings/errors; 17 connected service operations tests passed. All applicable browser variants passed; see the final evidence index below.

## Resource scope inventory

The administrative explorer composes the existing family Preview adapters for its mixed resource rows. It has one shared history/presentation shell with an explicit accepted-kind set; each family keeps its own exact loader, authorization, Summary and quick actions. No generic query or command registry is introduced. Canonical links now select overview and preserve the current administrative context. Internal/version/activity records without their own detail adapter retain native navigation without fabricated forms. Changing administrative context or selected ownership node closes/cancels inspection.

Validation: Release component/shared builds passed without warnings/errors; 6 scope-explorer component tests and 16 shared Preview history tests passed. All applicable browser variants passed; see the final evidence index below.

### Artifacts

Both durable and staged catalog branches expose native Open and Preview. They remain read-only saved records; retention, purge, content, record reads and materialization keep their existing full-page workflows and permission checks. Both summaries are shared with full overviews. Durable identity uses actual Core metadata. A staged metadata projection carries actual UID, namespace, owner, labels and governance alongside its existing safe view, without exposing backend references. Exact reads reject a different current execution Workspace or namespace before loading metadata. The UI preserves the logical administrative navigation context without promoting an internal operational Workspace to a user scope.

List rendering loads its existing two metadata catalogs only. Preview adds one selected metadata request and no content, lease, record, materialization or producer lookup. Context replacement cancels and clears the preview. Both artifact branches passed all four browser variants; see the final evidence index below.

### Execution history

ExecutionSummary is a bounded projection of RuntimeRun activity: run ID, execution status, start/duration and result/error. Its native `/runs/{id}` destination is operational diagnostics, with no saved definition, namespace or standalone authoring form. Retain native run inspection and server paging; no resource-definition Summary or Edit is manufactured.

Regression ownership: `RuntimeObservabilityConsoleTests`. This is an explicit non-resource decision for FR #1257, not an unverified adoption claim.

### Runtime placements

AgentInstanceResponse is the current placement snapshot for a run and worker, not a persisted Agent definition. Instance ID, execution scope, run/revision, worker and capacity retain their runtime meanings. Existing Agent, run and worker links remain; no saved resource metadata or placement definition editor exists.

Regression ownership: `RuntimeObservabilityConsoleTests`. This is an explicit non-resource decision for FR #1257, not an unverified adoption claim.

### Role assignments

AccessRow is the join of a member principal and a role assignment at an explicit/inherited identity scope. Its identity belongs to that relationship, not to a Core resource namespace/name. Retain the member administration destination and existing role/authorization workflows; a resource Preview would fabricate a definition address.

Regression ownership: `OrganizationAccessCollectionTests`. This is an explicit non-resource decision for FR #1257, not an unverified adoption claim.

### Identity membership and accounts

OrganizationMemberResponse, LocalAccountResponse and external-identity rows are identity-domain principals, credentials and membership relationships. Their existing administration page intentionally uses principal/account IDs and guarded role/status/password workflows, without a resource namespace, resource version or definition form. Retain those native pages and permissions; no Core resource metadata or generic resource Edit is invented.

Regression ownership: `OrganizationMembersCollectionTests / OrganizationMemberDetailsTests`. This is an explicit non-resource decision for FR #1257, not an unverified adoption claim.

### Security audit events

SecurityAuditEvent is append-only event evidence with action, outcome, actor/target IDs and occurrence time. The scope records where an event happened, not resource ownership. It has no inspectable saved definition or edit command; retain bounded audit snapshot and invariant identifiers.

Regression ownership: `SecurityAuditCollectionTests`. This is an explicit non-resource decision for FR #1257, not an unverified adoption claim.

### Cleanup resource candidates

Agent, Flow and Entry deletion candidates with known owners offer saved Preview through existing adapters. Native run/task candidates remain operational evidence. Candidate rows do not infer governance or definition editability; family quick actions resolve saved authorization. Current administrative context is retained. Deletion ordering, selection and confirmation/ETag behavior stay independent. Context replacement closes Preview and rejects stale inventory responses.

### Produced Flow Artifacts

The native run output catalog contains persisted durable Artifact resources. Its direct Inspect stays available alongside Open and Preview. The run host owns the Preview shell outside lazily visited sections, so direct/reload/history fragments work before the output tab has been visited. The existing exact Artifact adapter and saved Summary are reused; output JSON, run filtering, content and materialization behavior stay native.

### Saved Data Source Queries

These embedded rows are persisted Query resources with independent metadata and an existing JSON definition editor. Open selects the saved query overview in the native Data Source queries section. Edit definition selects that same existing form. Native links carry the actual query owner, namespace and name alongside the saved parent source reference; no catalog scan resolves a direct target. Preview makes one existing exact Query GET, without history, source, content, picker or execution reads. The saved Summary is also rendered above the native editor and never reads its unsaved JSON draft. Managed/inherited rows cannot use the edit shortcut.

Embedded Preview hosts live outside lazily visited section content. ResourceNavigation reserves `#preview/...` for the collection shell, preserves the current section and does not rewrite its history entry. On a direct Preview URL the host can mount independently of its embedded catalog having been opened.

### Extension materialized Tools

The contribution view contains actual persisted Tool resources, distinct from bundle membership/version-reference records. Known unambiguous Tools now offer Open and Preview through the existing Tool adapter. Conflicts continue to suppress target actions. The full Extension host owns the shell outside lazily visited contribution sections, preserving direct Preview restoration without visiting that section first.

### Extension Source Providers

Existing Source provider instances in Extension row actions have exact persisted scope/namespace/name metadata. Their existing Open command now explicitly selects overview; an adjacent Preview uses the existing SourceProvider adapter. Missing instances keep the original Configure command. These actions remain inside the registration row menu without nested menus or per-row detail loads.

## Final validation and evidence

Started from main `519693966364e56b29cdc820161e8d41f2c3059a`. Delivery retains one commit per assessed list: 42 adoptions and five explicit non-resource decisions. All product/Razor builds completed before the final browser campaigns; no product outputs were rebuilt during those campaigns.

Required restore and full Release build passed with zero warnings/errors. Fast: **3,083 passed**. Integration: **1,555 passed, 28 explicit optional skips, zero failed**; PostgreSQL and live Foundry/LocalAI/llama.cpp opt-ins were not configured. TypeScript, 23 JavaScript component checks and five fixture checks passed. Existing browser smoke, full campaign plus targeted rerun after governed inventory page-object maintenance: **494 passed, 0 skipped, 0 unexpected failures** across 494 cases. The independent route/collection inventory checks pass. [validation.json](evidence/1257/validation.json) records commands, result counts, resolved validation findings and normalized product source hashes.

Ad hoc Preview smoke: **184 cases passed** across the 42 adopted lists, with desktop/mobile, English/French, both themes, keyboard/touch, exact direct links, applicable Open/Edit and Back, reload, disclosure and bounds checks. Scrolling and the reachable fixed close control are exercised before captures return to the top of the saved Summary. An inherited off-window Parameter is inspected by its exact address without fabricating an ancestor inventory row. Per-case observations record the actual assertions and read-only exceptions. No unhandled circuit errors remained in the deterministic Preview host.

Additional evidence for the remaining lists and adapters:

- [Agent](evidence/1257/agents/README.md)
- [Artifacts](evidence/1257/artifacts/README.md)
- [Extension](evidence/1257/extensions/README.md)
- [Flow](evidence/1257/flows/README.md)
- [InstalledPack](evidence/1257/packs/README.md)
- [ResourcePlan](evidence/1257/resource-plans/README.md)
- [RuntimeWorker](evidence/1257/runtime-workers/README.md)
- [PackProject](evidence/1257/pack-projects/README.md)
- [Entry](evidence/1257/entries/README.md)
- [Model](evidence/1257/models/README.md)
- [Tool (Provider embedded)](evidence/1257/tools/provider-tools/README.md)
- [ConnectedServiceOperation](evidence/1257/connected-service-operations/README.md)
- [Resource scope inventory](evidence/1257/resource-scopes/README.md)
- [Cleanup candidates](evidence/1257/cleanup/README.md)
- [Flow output Artifacts](evidence/1257/flow-output-artifacts/README.md)
- [Data Source Queries](evidence/1257/data-source-queries/README.md)
- [Extension materialized Tools](evidence/1257/extensions/materialized-tools/README.md)
- [Extension Source Providers](evidence/1257/extensions/source-providers/README.md)

The new reusable scenario remains deferred under [#1258](https://github.com/gbaudrit/agentstration/issues/1258), as required by `automation/playwright/AGENTS.md` and the feature’s recorded UX gate. It requires explicit maintainer UX validation and the owning UX PR merge before implementation. These implementation checks do not close #1257 or #1258.

## CI packaging checks

On 22 November 2026, the official .NET 10.0.300 formatter verified all **346 changed C#/Razor files** after correcting layout and import order in 54 C# files. Source and literal content are preserved after ignoring layout and import order. The full documentation build, including ADR validation, LikeC4 validation/code generation and Docusaurus static generation, passed after marking the `/runs/{id}` route as inline code.

The functional/browser evidence above remains attached to validated revision `3db77ef35d3d2d636481b37db06a4b1ec44b8758`. `validation.json` retains those original source hashes and records the four formatted product files separately; this formatting-only correction does not claim a new browser campaign.

The existing acquisition/Knowledge host-restart integration test also waits for an in-flight hosted recovery to reach a terminal state before asserting success and retained identities/content. The wait is bounded and preserves the original assertions. The focused test passed three consecutive executions after a Release build with zero warnings/errors.

The branch was subsequently rebased onto main `0a986f3f` (including #1268 and #1271). Both sets of Connected Service translations were retained with valid XML and unique, symmetric English/French keys. The affected Release builds passed without warnings/errors, and **105 focused checks passed**: 102 Console component cases, two operation-access API cases and the restart-recovery case. The original rollout evidence remains attached to its recorded source revision; the current full CI campaign is available on [#1273](https://github.com/gbaudrit/agentstration/pull/1273).
