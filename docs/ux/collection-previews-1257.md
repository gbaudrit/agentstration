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

Validation: Release component/test build passed without warnings/errors; 137 data sources tests passed. Browser and final offline checks remain pending.
