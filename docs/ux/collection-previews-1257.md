# Resource collection Actions, saved Summaries and Preview

Implementation of [#1257](https://github.com/gbaudrit/agentstration/issues/1257), starting at main `51969396`. The [adoption/exception matrix](collection-previews-1257.json) retains every frame and specialized surface from `collection-surfaces-1121.json`, including embedded and operational lists. A pending entry is unfinished work, not an exception. Each list is delivered in its own commit; existing Agents/Flows/Extensions implementations are reviewed as baselines.

## Ownership and navigation

`CollectionActions` and `ResourceCollectionPreview` own menu and modal/history presentation. Native hashchange/popstate is observed as well as Blazor navigation, so a closed Preview can reopen through the same fragment without a reload. The observer changes presentation only and never pushes/replaces navigation or bypasses dirty-form guards. `ResourceSummary` supplies the shared spacing and wrapping two-column saved-configuration layout in both full details and Preview. Each screen supplies its own authorized row actions and exact resource address. A family Preview owns the bounded scoped snapshot read, rejects returned owner/namespace/kind/name mismatches, and cancels/rejects superseded requests. It never scans collection pages or loads editor pickers. Summary receives saved values and independently supplied quick actions; it owns no reads or mutations.

Names and Open select `#overview`; Edit definition selects the existing definition form. `#preview/{kind}/{encoded-owner}/{namespace}/{name}` preserves the current collection query and supports direct/off-window targets. Open/Edit leave that history entry available for browser Back. Explicit close replaces the fragment; context replacement closes the old Preview. Existing return-to-list links remain explicit collection links and dirty-form guards remain in place.

Summaries reuse the template's Identity fragment in full details and render a complete copyable standalone Identity in Preview, including labels and applicable Extension provenance. Identity and Configuration initially open in Preview and remain independently foldable without unmounting child content or refetching. Preview hides the Configuration view selector while preserving automatic narrow-screen cards.

## Parameters

The list adds Open, authorized Edit definition and Preview. There is no row Delete command to duplicate the full editor's usage/ETag workflow. The shared saved Summary shows value type and readable value; it never resolves secrets or performs usage/picker reads. The endpoint only supports the default namespace, so other namespaces are rejected before any read. Exact owner is mandatory. The overview retains its existing usage metric and saved/draft lifecycle.

Validation: Console component/test project Release build passed with zero warnings/errors; 41 Parameter component/model/collection/resource-screen/Preview tests passed. All four desktop/mobile English/French light/dark browser variants passed; [observations](evidence/1257/parameters/README.md). Final offline lanes remain pending. New reusable browser journeys remain deferred in native child [#1258](https://github.com/gbaudrit/agentstration/issues/1258) until the required maintainer UX validation and merge. Neither issue is closed by these implementation increments.

## Schema

Saved format, dialect, schema content and Source evidence. No formats catalog or version-history read in Preview. The family Preview uses `ISchemasClient.GetSchemaAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed with zero warnings/errors; 91 Schema tests passed, including exact-address Preview, bilingual disclosure and existing editor/collection regressions. All four desktop/mobile English/French light/dark browser variants passed; [observations](evidence/1257/schemas/README.md). Final offline lanes remain pending.

## Vault

Saved Vault identity, provider type and descendant-use policy; initialization and key operations stay on the full detail page. The family Preview uses `ISecretsClient.GetVaultAsync(name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 43 Vault tests passed. All four desktop/mobile English/French light/dark browser variants passed; [observations](evidence/1257/vaults/README.md). Final offline lanes remain pending.

## Secret

Saved Secret metadata, Vault reference, key, localized type and descendant-use policy. Secret contents are never read or displayed; value operations and usages stay on the full detail page. The family Preview uses `ISecretsClient.GetSecretAsync(name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 40 Secret tests passed. All four desktop/mobile English/French light/dark browser variants passed; [observations](evidence/1257/secrets/README.md). Final offline lanes remain pending.

## ConnectedService

Saved identity, service type/version, literal options and Parameter/Secret references; no resolved secret values, catalog/discovery/usages fetch or editor draft. Reconcile and deactivation retain the full detail confirmation/ETag workflow. The family Preview uses `IConnectedServicesClient.GetConnectedServiceAsync(name, owner, namespace, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 56 Connected Service tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/connected-services/README.md). Final offline lanes remain pending.

## StorageType

Saved identity, enabled flag, preferred immutable version, observed generic-use decision and access policy. Full details retain usages and version-contract panels without fetching usages/history for Preview. The independently supplied Preview quick actions add the existing version publication form. The family Preview uses `IStorageTypesClient.GetTypeAsync(exact address, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 54 storage types tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/storage-types/README.md). Final offline lanes remain pending.

## Storage

Saved identity, pinned type/version, configuration/envelope, collections, access policy and declared Connected Service/export mappings. Preview never loads the type catalog, usages, diagnostics or document contents. The full overview retains its resolved type display name. The family Preview uses `IStoragesClient.GetAsync(exact address, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 140 storages tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/storages/README.md). Final offline lanes remain pending.

## DataSourceProfile

Saved identity, actual known publication version, acquisition/discovery Flow references, configuration schema, tool bindings, limits and policies. Preview does not fetch revisions, Flow catalogs or pickers. Inherited row links retain the current administrative context. The family Preview uses `IDataSourceProfilesClient.GetAsync(namespace, name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 38 data source profiles tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/data-source-profiles/README.md). Final offline lanes remain pending.

## RuntimeProfile

Saved identity, runtime type, execution/session/tool/streaming defaults and runtime options. Preview reads one exact snapshot without usages or editor pickers. Row and full-detail URLs retain exact owner/namespace and the current administrative context. The existing service exact-read capability is now exposed by the client and HTTP GET, with no unscoped fallback. The family Preview uses `IRuntimeProfilesClient.GetRuntimeProfileAsync(namespace, name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 31 runtime profiles tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/runtimeprofiles/README.md). Final offline lanes remain pending.

Runtime Profile HTTP GET now accepts the existing exact `scopeRef` plus `resourceNamespace` address. It preserves the Instance-only scope policy and legacy requests without an explicit owner. Focused API integration passed: exact namespace homonyms and ETags, hidden unshared Instance resources from a Workspace context, and missing/unsupported exact targets without fallback.

## ToolProvider

Saved identity, localized provider type, enabled state, MCP transport/endpoint or command/argument/environment references and AEP Extension reference. Full details retain the existing ownership banner, discovery, tests, toggles and discovered Tools. Preview does not probe a provider or enumerate Tools. The family Preview uses `IToolsClient.GetProviderAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 34 tool providers tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/tools/providers/README.md). Final offline lanes remain pending.

## Tool

Saved identity/configuration, capability, input/output schema summaries, observed discovery facts and known governance/provider references. Provider/Extension observations are optional saved caller inputs on the full overview, never loaded by Preview. Tools have a read-only inspection definition: Edit definition is not applicable. Preview independently offers the existing execution form only for enabled, available Tools with RunsExecute; it never invokes a Tool. The family Preview uses `IToolsClient.GetToolAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 35 tools tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/tools/README.md). Final offline lanes remain pending.

## ToolSet

Saved identity, known published version, draft version/publication request, category, exact declared members and annotations. Tool Sets expose an existing read-only definition inspection, so Edit definition is not applicable. Preview independently links to the existing publication history without reading version lists until navigation; deletion remains in the full detail. The family Preview uses `IToolSetsClient.GetToolSetAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 37 tool sets tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/tools/sets/README.md). Final offline lanes remain pending.

## ToolCategory

Saved identity and declared Tool membership references, independent of the editor selection/draft. Preview adds one exact Workspace/namespace/name read through the existing store; it never loads resolved members, Tools or Provider pickers. Full editor reads now use the exact owner when supplied. The family Preview uses `IToolCategoriesClient.GetCategoryAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 30 tool categories tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/tools/categories/README.md). Final offline lanes remain pending.

## ToolDefinition

Saved identity and the existing Flow, Relay or Acquisition configuration. Both full detail adapters reuse the same saved Summary. Preview reads no Flow contracts, source catalogs, version history, usages or pickers; a Relay source generation is displayed as a catalog revision unless an actual known version is supplied. The family Preview uses `IToolDefinitionsClient.GetAsync(owner, name, namespace, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 95 tool definitions tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/tools/definitions/README.md). Final offline lanes remain pending.

## ModelProvider

Saved identity, Extension/contribution reference, value bindings and specification overrides. Preview reads one exact Instance/namespace/name snapshot, without testing providers, discovering models or loading Extension/Parameter/Secret pickers. ModelProvider remains Instance-only under its existing scope policy. The family Preview uses `IModelProvidersClient.GetModelProviderAsync(namespace, name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 123 model providers tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/modelproviders/README.md). Final offline lanes remain pending.

## ModelProfile

Saved identity, Provider/Model reference, generation/reasoning/output options, stop sequences and provider options. The full overview may supply an already known Provider owner; Preview does not infer one from another catalog. Exact Preview avoids model resolution, Extension/Provider/Model pickers, capabilities and usage reads. Existing row deletion retains its authorization, ETag and confirmation workflow. The family Preview uses `IModelProfilesClient.GetModelProfileAsync(namespace, name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 132 model profiles tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/modelprofiles/README.md). Final offline lanes remain pending.

## DataSource

Saved identity and Profile-based or Dataset acquisition configuration, shared by both detail adapters. Optional already resolved service/operation/storage display names and observations stay in the full overview. Preview uses one exact snapshot and does not read readiness, operation contracts, service/storage catalogs, acquisitions, discovery or document contents. The family Preview uses `IDataSourcesClient.GetAsync(namespace, name, owner, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 137 data sources tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/data-sources/README.md). Final offline lanes remain pending.

## KnowledgeSource

Saved identity, exact declared Flow references, projection parameters, input bindings, output storage pin and lifecycle/composition policy. Both full details and the standalone edit adapter reuse the saved Summary. Preview reads no readiness, resolved Flows, snapshots, Tool exposure, data-source/Storage catalogs or document content. Full-detail observations remain supplied by the owning page. The family Preview uses `IKnowledgeSourcesClient.GetAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 107 knowledge sources tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/knowledge-sources/README.md). Final offline lanes remain pending.

## SourceProvider

Saved identity and declared Extension/contribution reference. Existing exact owner/namespace/name reads are reused; legacy client implementations now reject unsupported exact reads instead of silently ignoring the owner. Preview does not inspect Extension connections, enumerate contributions, probe the provider or read usages. Catalog Platform administrator authorization remains enforced by the existing endpoint. The family Preview uses `ISourceProvidersClient.GetSourceProviderAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 34 source providers tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/sourceproviders/README.md). Final offline lanes remain pending.

## SourceRegistryRegistration

Saved Instance/default-namespace registration identity, endpoint and credential reference, trust, endpoint, refresh and cache policies. The new exact desired-state endpoint reads only the selected registration; no observed-state provisioning, refresh, index download, discovery cache or history read. Existing full-detail observation panels and row toggle/refresh commands are retained. Other namespaces and unsupported owners never fall back. The family Preview uses `ISourceRegistriesClient.GetRegistrationAsync(owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 40 source registries tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/settings/source-registries/README.md). Final offline lanes remain pending.

## Source

Separate collection/detail route adapters preserve disposal and Preview history for the shared Sources screen. Saved Source identity, publisher, origin, local bindings and refresh policies. Full details additionally supply the already selected immutable publication and its declared channels. The exact Source Summary endpoint reads only Source and local SourceConfiguration by owner/publisher/name; it does not enumerate versions, providers, catalog materializations or channels. Preview omits unknown publication facts instead of resolving history. Existing full-detail verification/import observations remain caller-supplied. The family Preview uses `ISourceConsoleApiClient.GetSavedSummaryAsync(owner, namespace/publisher, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 98 sources tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/settings/sources/README.md). Final offline lanes remain pending.

## Trigger

Saved Trigger identity, schedule, target, resolution and execution policy/input. Preview displays the logical administrative context, retaining the physical operational Workspace only in its exact address. The server rejects a requested physical owner different from the current operational context; no scope fallback, firing, schedule preview, history or Flow catalog read. Full details retain their existing failure observation and execution/history panels. The family Preview uses `ITriggerApiClient.GetAsync(exact physical owner, namespace, name, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 71 triggers tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/triggers/README.md). Final offline lanes remain pending.

## TransformationFunction

Saved identity, enabled state and immutable version-to-kind/configuration references. Full details may supply already loaded native contract observations; exact Preview does not load implementations, Function Kind catalogs, editor options or mutation state. The family Preview uses `ITransformationFunctionsClient.GetAsync(exact FunctionReference, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 34 transformation functions tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/transformation-functions/README.md). Final offline lanes remain pending.

## FunctionKind

The separate Function Kind catalog retains its existing immutable contract expansion and guarded activation command inside the row menu, and adds Open/Preview. A read-only addressable detail composes the same saved Summary through OverviewSummary. Preview performs only the exact kind read and never loads implementation choices or mutates activation. The native version/executor/schema dataset remains explicitly inventoried.

Validation: Release component/test build passed without warnings/errors; 48 function kinds tests passed. All four desktop/mobile English/French light/dark browser variants passed, including keyboard and mobile touch; [observations](evidence/1257/function-kinds/README.md). Final offline lanes remain pending.

## InstalledPack

Saved installation identity/version, destination namespace, purpose/audience, source/installation facts, persisted Source provenance and declared binding/managed-resource records. The existing native management panel and new read-only details reuse the same saved Summary. Preview reads exactly one canonical Pack address through the existing scoped endpoint; malformed/non-default-namespace addresses never scan or fall back. Installation management remains a separate supplied navigation action for authorized owned installations, with its existing dialogs/ETags. The family Preview uses `IPacksClient.GetPackAtAddressAsync(exact address, token)` with exact address validation and cancellation. Its overview reuses the saved Summary, retaining existing metrics/forms/commands. No mutation is introduced in the row menu.

Validation: Release component/test build passed without warnings/errors; 70 installed packs tests passed. Browser and final offline checks remain pending.

## PackProject

The embedded project catalog gets independent Open, authorized Edit and Preview actions. Both overview and Preview share the native saved metadata and source provenance. Exact reads use the Workspace owner, default namespace and persisted GUID N name, retain the independent server-assigned UID for the full-detail route, and do not enumerate projects, archives, source documents or builds. Existing editing/build/installation guards remain in the full view.

Validation: Release component/test build passed without warnings/errors; 82 pack projects tests passed. Browser and final offline checks remain pending.

## RuntimeWorker

Workers use their existing Instance/default AwpWorkerIdentity enrollment coordinates (WorkerId N), with native GUID detail URLs. Row and quick actions offer inspection; there is no business definition form. The pure Summary shares native identity, compatibility and capacity facts with full details, and omits unavailable UID/labels/publication facts. The lazy endpoint reads one enrollment, one in-memory registration and a WorkerId-filtered bounded active-assignment count; it never scans worker catalogs or loads recent assignment history. PlatformAdmin authorization remains on the API.

Validation: Release component/test build passed without warnings/errors; 29 runtime workers tests passed. Browser and final offline checks remain pending.

## Entry

The native Entries card catalog gets Open, owned/unmanaged Edit and lazy Preview. Overview and Preview reuse saved draft configuration and the current persisted publication, with native Workspace/EntryId coordinates. The endpoint rejects a different execution Workspace before reading, and loads only the draft and current publication, without pickers, usage, validation, publication or Pack-catalog scans. Logical scope replaces internal operational Workspace presentation. Preview Edit requires the loaded catalog's known non-managed flag; a direct link outside that loaded catalog offers Open until the full editor resolves governance. No unknown management state is treated as editable. No UID or labels are invented for this native Work resource.

Validation: Release component/test build passed without warnings/errors; 39 entries tests passed. Browser and final offline checks remain pending.

## Model

Persisted Models embedded in the Provider catalog get Open and Preview alongside the existing guarded Create profile link. Discovery choices without ResourceName remain value choices, without fabricated addresses. Models are immutable observed resources: no Edit definition is invented. The shared native Summary renders provider identity, capabilities and limits from the saved snapshot. Full details may supply already-known saved override/effective facts and its existing override action; Preview never fetches the Provider, discovery/history or override editor. The new exact Instance/namespace/name GET preserves the full detail's provider UID/ETag override workflow.

Validation: Release component/test build passed without warnings/errors; 130 models tests passed. Browser and final offline checks remain pending.

## Agent (baseline completion)

The namespaced Pack read-only adapter now supplies its template Identity to the same saved Agent Summary used by the editor and Preview. Its native forms, Pack provenance, original source, model/runtime diagnostics and prepare-runtime commands remain in place; bootstrap namespaced resources keep their existing editor redirect. Row/Preview Edit follows that real redirect for owned unmanaged namespaced resources. Saved summaries retain complete Tool/ToolSet owners/members, behaviors and both schema bindings and inline contracts. References with unknown owners stay text unless full details supply a known exact destination.

Validation: Release component/test build passed without warnings/errors; 74 agents tests passed. Browser and final offline checks remain pending.

## Flow (baseline verification)

The existing exact-address Summary/Preview retains saved topology, current publication version and independent Designer navigation. No history read supplies an unknown publication date. Row ownership now also follows the explicit current context, and permission/context notifications clear or cancel a mounted adapter independently of its list shell. Read-only presentation follows actual editing availability.

Validation: Release component/test build passed without warnings/errors; 91 flows tests passed. Browser and final offline checks remain pending.

## Extension (baseline completion)

Manual/managed registrations keep independent native actions and their exact saved Preview. Standalone identity now includes actual read-only/governance and Extension provenance. Unregistered AEP enrollment rows are inspectable resources too: a second adapter reads their existing Instance/default/InstanceId N address and reuses the same pure Summary as full details. The endpoint preserves existing enrollment authorization, target visibility and sanitization; it never reads credentials, probes endpoints or issues pairing/rotation commands. Enrollments have no invented definition form.

Validation: Release component/test build passed without warnings/errors; 118 extensions tests passed. Browser and final offline checks remain pending.
