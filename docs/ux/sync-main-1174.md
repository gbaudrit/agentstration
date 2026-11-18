# Main synchronization and new-surface adoption — #1174

Baseline: previous main `cc8e906e1b833e86e365c549ce4c9696376f3407`; incoming main `8f1645446d7ae9082ed2d00bd28cce98ebaa023a` (#1181). The existing promotion head was `8b568e3633005062199268e56b76a2e5abb4a20d`. Main is merged into the dedicated PR branch without rewriting either history. The maintainer merged coverage PR #1132 into the UX integration branch (`2c18b47f`); its `f80a1758` content was already an ancestor of this promotion. All subsequent corrections stay in #1174. No child-branch retarget or duplicate coverage delivery is needed.

The approved all-list contract applies to new Connected Service, Storage, Storage Type and transformation-function inventories, their embedded details, source datasets, query/tool projections and Flow outputs. Specialized editors, ordered composition, causal trees and execution progress retain their domain interaction. Generations, installed type versions, immutable query versions and execution attempts are not publication versions.

The scope below audits every added/changed Razor source in the incoming delta, including shared helpers and existing screens. `—` denotes an embedded component, not an extra route. Route aliases share one functional source. No Workplace Razor file changed in this incoming main commit; the existing Workplace implementation and regression cases remain in the merged branch.

## CI follow-up after merged #1219

Main `eb0a616429ee4f1195160705bd60e48a6699b3ce` is now merged into the promotion branch. The merge is conflict-free and retains all UX sources and assertions. It includes the Resources/DataSources/Artifacts process split, shared test-root fixtures, complete Integration/coverage registration and unchanged 2,432 MiB ceilings. The earlier CI memory failure below belongs to the previous head. Fresh validation of this synchronized head is recorded in #1174; the old green UX result is not substituted for that run.

## Incoming source/route reconciliation

| Change | Source | Actual routes / mounting |
| --- | --- | --- |
| A | [ContractFailurePanel.razor](../../src/Agentstration.Console.Components/Components/ContractFailurePanel.razor) | — embedded/shared component |
| A | [ExecutionProgress.razor](../../src/Agentstration.Console.Components/Components/Flows/ExecutionProgress.razor) | — embedded/shared component |
| A | [FlowProcessSummary.razor](../../src/Agentstration.Console.Components/Components/Flows/FlowProcessSummary.razor) | — embedded/shared component |
| A | [FlowRunCausalityOutput.razor](../../src/Agentstration.Console.Components/Components/Flows/FlowRunCausalityOutput.razor) | — embedded/shared component |
| A | [FlowRunOutput.razor](../../src/Agentstration.Console.Components/Components/Flows/FlowRunOutput.razor) | — embedded/shared component |
| M | [AgentEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/AgentEditor.razor) | `/agents/new`<br />`/agents/{Name}` |
| M | [Artifacts.razor](../../src/Agentstration.Console.Components/Components/Pages/Artifacts.razor) | `/artifacts` |
| A | [ConnectedServiceDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/ConnectedServiceDetails.razor) | `/connected-services/{Name}` |
| A | [ConnectedServiceDiscoveryPanel.razor](../../src/Agentstration.Console.Components/Components/Pages/ConnectedServiceDiscoveryPanel.razor) | — embedded/shared component |
| A | [ConnectedServiceEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/ConnectedServiceEditor.razor) | `/connected-services/new`<br />`/connected-services/{Name}/edit` |
| A | [ConnectedServices.razor](../../src/Agentstration.Console.Components/Components/Pages/ConnectedServices.razor) | `/connected-services` |
| A | [DataSourceAcquisitionDocuments.razor](../../src/Agentstration.Console.Components/Components/Pages/DataSourceAcquisitionDocuments.razor) | — embedded/shared component |
| A | [DataSourceDatasetEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/DataSourceDatasetEditor.razor) | — embedded/shared component |
| M | [DataSourceEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/DataSourceEditor.razor) | `/data-sources/new`<br />`/data-sources/{Name}` |
| A | [DataSourceQueries.razor](../../src/Agentstration.Console.Components/Components/Pages/DataSourceQueries.razor) | — embedded/shared component |
| M | [DataSources.razor](../../src/Agentstration.Console.Components/Components/Pages/DataSources.razor) | `/data-sources` |
| M | [FlowDesigner.razor](../../src/Agentstration.Console.Components/Components/Pages/FlowDesigner.razor) | `/flows/{FlowId}/designer`<br />`/namespaces/{FlowNamespace}/flows/{FlowId}/designer` |
| M | [FlowRunArtifactDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/FlowRunArtifactDetails.razor) | `/artifacts/durable/{Id}` |
| M | [FlowRunDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/FlowRunDetails.razor) | `/flow-runs/{RunId}` |
| M | [FlowRuns.razor](../../src/Agentstration.Console.Components/Components/Pages/FlowRuns.razor) | `/flow-runs` |
| A | [FlowSequenceEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/FlowSequenceEditor.razor) | — embedded/shared component |
| A | [FlowTransformationCreator.razor](../../src/Agentstration.Console.Components/Components/Pages/FlowTransformationCreator.razor) | — embedded/shared component |
| A | [KnowledgeContentAccess.razor](../../src/Agentstration.Console.Components/Components/Pages/KnowledgeContentAccess.razor) | — embedded/shared component |
| M | [KnowledgeSourceDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/KnowledgeSourceDetails.razor) | `/knowledge-sources/{Name}` |
| M | [KnowledgeSourceEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/KnowledgeSourceEditor.razor) | `/knowledge-sources/new`<br />`/knowledge-sources/{Name}/edit` |
| M | [Management.razor](../../src/Agentstration.Console.Components/Components/Pages/Management.razor) | `/management` |
| A | [NewToolDefinition.razor](../../src/Agentstration.Console.Components/Components/Pages/NewToolDefinition.razor) | `/tools/definitions/new` |
| A | [OperationSchemaCandidate.razor](../../src/Agentstration.Console.Components/Components/Pages/OperationSchemaCandidate.razor) | — embedded/shared component |
| M | [ResourceAccessPanel.razor](../../src/Agentstration.Console.Components/Components/Pages/ResourceAccessPanel.razor) | — embedded/shared component |
| M | [ResourceScopes.razor](../../src/Agentstration.Console.Components/Components/Pages/ResourceScopes.razor) | `/settings/resource-scopes` |
| M | [SchemaEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/SchemaEditor.razor) | `/schemas/new`<br />`/schemas/{Name}` |
| A | [SourceFlowCompositionEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/SourceFlowCompositionEditor.razor) | — embedded/shared component |
| A | [SourceLifecycleEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/SourceLifecycleEditor.razor) | — embedded/shared component |
| M | [StagedArtifactDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/StagedArtifactDetails.razor) | `/artifacts/staged/{Id}` |
| A | [StorageCollectionsEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageCollectionsEditor.razor) | — embedded/shared component |
| A | [StorageConfirmation.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageConfirmation.razor) | — embedded/shared component |
| A | [StorageDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageDetails.razor) | `/storages/{Name}` |
| A | [StorageDocuments.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageDocuments.razor) | — embedded/shared component |
| A | [StorageEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageEditor.razor) | `/storages/new`<br />`/storages/{Name}/edit` |
| A | [StorageError.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageError.razor) | — embedded/shared component |
| A | [StorageTypeDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageTypeDetails.razor) | `/storage-types/{Name}` |
| A | [StorageTypeEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageTypeEditor.razor) | `/storage-types/new`<br />`/storage-types/{Name}/edit`<br />`/storage-types/{Name}/versions/new` |
| A | [StorageTypeEvidence.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageTypeEvidence.razor) | — embedded/shared component |
| A | [StorageTypeVersionFields.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageTypeVersionFields.razor) | — embedded/shared component |
| A | [StorageTypes.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageTypes.razor) | `/storage-types` |
| A | [Storages.razor](../../src/Agentstration.Console.Components/Components/Pages/Storages.razor) | `/storages` |
| M | [ToolDefinitionEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/ToolDefinitionEditor.razor) | `/tools/definitions/{Name}` |
| M | [ToolDefinitions.razor](../../src/Agentstration.Console.Components/Components/Pages/ToolDefinitions.razor) | `/tools/definitions` |
| M | [ToolDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/ToolDetails.razor) | `/tools/{Name}` |
| A | [ToolRelayEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/ToolRelayEditor.razor) | — embedded/shared component |
| A | [TransformationFunctionEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/TransformationFunctionEditor.razor) | `/transformation-functions/{Name}` |
| A | [TransformationFunctions.razor](../../src/Agentstration.Console.Components/Components/Pages/TransformationFunctions.razor) | `/transformation-functions`<br />`/function-kinds` |
| M | [TriggerDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/TriggerDetails.razor) | `/triggers/{Name}` |
| M | [RunGrid.razor](../../src/Agentstration.Console.Components/Components/RunGrid.razor) | — embedded/shared component |
| A | [StructuredDocumentQueryEditor.razor](../../src/Agentstration.Console.Components/Components/StructuredDocumentQueryEditor.razor) | — embedded/shared component |
| M | [ValueBindingRequirementEditor.razor](../../src/Agentstration.Console.Components/Components/Values/ValueBindingRequirementEditor.razor) | — embedded/shared component |
| M | [_Imports.razor](../../src/Agentstration.Console.Components/Components/_Imports.razor) | — embedded/shared component |
| M | [ConfirmationDialog.razor](../../src/Agentstration.Web.Components/ConfirmationDialog.razor) | — embedded/shared component |
| A | [FunctionDescription.razor](../../src/Agentstration.Web.Components/FunctionDescription.razor) | — embedded/shared component |
| M | [JsonSchemaField.razor](../../src/Agentstration.Web.Components/JsonSchema/JsonSchemaField.razor) | — embedded/shared component |
| M | [JsonSchemaInputEditor.razor](../../src/Agentstration.Web.Components/JsonSchema/JsonSchemaInputEditor.razor) | — embedded/shared component |
| M | [FlowAgentInspector.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowAgentInspector.razor) | — embedded/shared component |
| A | [FlowArtifactTransformInspector.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowArtifactTransformInspector.razor) | — embedded/shared component |
| M | [FlowCanvas.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowCanvas.razor) | — embedded/shared component |
| A | [FlowCompositionInspector.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowCompositionInspector.razor) | — embedded/shared component |
| A | [FlowDataMappingEditor.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowDataMappingEditor.razor) | — embedded/shared component |
| M | [FlowDesigner.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowDesigner.razor) | — embedded/shared component |
| A | [FlowForeachInspector.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowForeachInspector.razor) | — embedded/shared component |
| M | [FlowNodeWidget.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowNodeWidget.razor) | — embedded/shared component |
| M | [FlowOutputInspector.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowOutputInspector.razor) | — embedded/shared component |
| M | [FlowStepPalette.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowStepPalette.razor) | — embedded/shared component |
| M | [FlowTopologyViewer.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowTopologyViewer.razor) | — embedded/shared component |
| M | [FlowTransformInspector.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowTransformInspector.razor) | — embedded/shared component |
| M | [SchemaBindingEditor.razor](../../src/Agentstration.Web.FlowDesigner/Components/SchemaBindingEditor.razor) | — embedded/shared component |

## Evidence and coverage boundaries

- The machine-readable [collection inventory](collection-surfaces-1121.json) records functional datasets, scope/action applicability and specialized adaptation. The [form inventory](formulaires-tous-ecrans/inventory.json) distinguishes routed adapters from embedded helpers.
- Existing route coverage is retained. Source policy/composition journeys are attached to their real host routes as additional assertions; embedded components are never assigned invented page routes. The catalog contains one entry per actual source/route pair, including the new Tool creation route and both function catalog aliases.
- Playwright enumerates 513 cases: 219 desktop, 218 mobile and 76 locale cases. This is enumeration, not a successful browser campaign. Existing cases are retained; new populated collection checks add one desktop and one mobile case to the synchronized 511-case catalog.
- New routed form field coverage is explicitly pending under #1122 until its assertions are implemented. Source adoption, local smoke and existing family tests do not automatically claim a complete reusable journey. The existing deferred-scenario and authorized stacked-draft exceptions remain applicable.
- Historical screenshots and test totals elsewhere retain their original source commits. They are not proof of the new main synchronization. Current build, offline lanes, desktop/mobile captures and CI results must be recorded by the final delivery.

## Current functional inventory

The reconciled source contains **88 shared frames and 258 active specialized datasets (346 active functional collections)**, plus four unchanged unmounted legacy renderers. The main delta adds 17 frames and 58 specialized datasets to the 271-active checkpoint. **145 repeated-item sources** are audited (31 newly audited, including two previously unguarded indexed controls). These counts describe functional datasets, not routes, pages, raw loops or viewport variants. The form inventory contains **62 routed adapters, 90 route patterns and 58 embedded helpers**.

| New specialized source | Explicit functional datasets |
| --- | --- |
| [StorageDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageDetails.razor) | `storage-details-diagnostics`<br />`storage-details-dependents` |
| [StorageTypeDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageTypeDetails.razor) | `storage-type-details-dependent-storages`<br />`storage-type-details-immutable-version-choice`<br />`storage-type-details-requirements` |
| [StorageTypeEvidence.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageTypeEvidence.razor) | `storage-type-evidence-query-profiles` |
| [Storages.razor](../../src/Agentstration.Console.Components/Components/Pages/Storages.razor) | `storages-type-version-evidence` |
| [StorageDocuments.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageDocuments.razor) | `storage-documents-collection-choices`<br />`storage-documents-published-tools`<br />`storage-document-query-results` |
| [StorageEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageEditor.razor) | `storage-editor-type-version-choices`<br />`storage-editor-connected-service-choices`<br />`storage-editor-export-choices` |
| [ToolRelayEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/ToolRelayEditor.razor) | `tool-relay-source-choices`<br />`tool-relay-source-version-choices`<br />`tool-relay-fixed-input-bindings` |
| [DataSourceQueries.razor](../../src/Agentstration.Console.Components/Components/Pages/DataSourceQueries.razor) | `dataset-query-publication-choices`<br />`dataset-query-resource-choices`<br />`dataset-query-retained-acquisition-choices`<br />`dataset-saved-query-results` |
| [KnowledgeContentAccess.razor](../../src/Agentstration.Console.Components/Components/Pages/KnowledgeContentAccess.razor) | `knowledge-content-snapshot-choices`<br />`knowledge-content-read-chunks`<br />`knowledge-content-regex-matches`<br />`knowledge-content-record-lineage`<br />`knowledge-content-input-freshness`<br />`knowledge-content-document-status`<br />`knowledge-document-query-results` |
| [DataSourceDatasetEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/DataSourceDatasetEditor.razor) | `dataset-connected-service-choices`<br />`dataset-operation-choices`<br />`dataset-storage-choices`<br />`dataset-document-collection-choices`<br />`dataset-acquisition-content-choices`<br />`dataset-manifest-artifacts` |
| [TransformationFunctionEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/TransformationFunctionEditor.razor) | `transformation-function-kind-choices`<br />`transformation-function-implementation-choices` |
| [ConnectedServiceEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/ConnectedServiceEditor.razor) | `connected-service-type-variant-choices` |
| [ConnectedServiceDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/ConnectedServiceDetails.razor) | `connected-service-dependent-resources` |
| [TransformationFunctions.razor](../../src/Agentstration.Console.Components/Components/Pages/TransformationFunctions.razor) | `function-kind-version-contracts` |
| [FlowSequenceEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/FlowSequenceEditor.razor) | `source-flow-function-choices`<br />`source-flow-child-flow-choices`<br />`source-flow-child-version-choices`<br />`source-flow-ordered-calls` |
| [SourceFlowCompositionEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/SourceFlowCompositionEditor.razor) | `source-flow-orchestration-choices`<br />`source-flow-composition-points` |
| [NewToolDefinition.razor](../../src/Agentstration.Console.Components/Components/Pages/NewToolDefinition.razor) | `new-tool-flow-choices`<br />`new-tool-flow-version-choices` |
| [FlowRunOutput.razor](../../src/Agentstration.Console.Components/Components/Flows/FlowRunOutput.razor) | `flow-run-produced-artifacts` |
| [FlowProcessSummary.razor](../../src/Agentstration.Console.Components/Components/Flows/FlowProcessSummary.razor) | `flow-process-ordered-units` |
| [ExecutionProgress.razor](../../src/Agentstration.Console.Components/Components/Flows/ExecutionProgress.razor) | `execution-progress-active-locations` |
| [FlowRunDetails.razor](../../src/Agentstration.Console.Components/Components/Pages/FlowRunDetails.razor) | `flow-run-owned-processes`<br />`flow-run-artifact-contracts` |
| [KnowledgeSourceEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/KnowledgeSourceEditor.razor) | `knowledge-source-output-storage-choices`<br />`knowledge-source-output-document-collection-choices` |
| [FlowTransformInspector.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowTransformInspector.razor) | `flow-transform-function-choices` |
| [FlowDataMappingEditor.razor](../../src/Agentstration.Web.FlowDesigner/Components/FlowDataMappingEditor.razor) | `flow-data-mapping-variable-options` |
| [StorageCollectionsEditor.razor](../../src/Agentstration.Console.Components/Components/Pages/StorageCollectionsEditor.razor) | `storage-collection-definition-rows` |
| [StructuredDocumentQueryEditor.razor](../../src/Agentstration.Console.Components/Components/StructuredDocumentQueryEditor.razor) | `structured-document-query-profile-choices` |
| [DataSourceAcquisitionDocuments.razor](../../src/Agentstration.Console.Components/Components/Pages/DataSourceAcquisitionDocuments.razor) | `dataset-document-query-results` |

The repeat-source guard now scans indexed `@for` loops as well as `@foreach`. This adds two incoming indexed helpers and two pre-existing indexed controls (fixed ResourcePageTabs and topology output ports) to the audit; both controls have an explicit non-collection reason. It does not count each loop as a functional collection.

## Current source/catalog checks

TypeScript and all seven application/form/collection catalog checks pass after reconciliation. The indexed-loop guard is stronger than the old foreach-only check. Current route ownership includes five focused list/editor adapters mapped to `connected-storage-collection.spec.ts`; six routed field adapters stay explicitly pending under #1122, including the retained definition editor whose former creation route is now a different component. No browser success is inferred from enumeration or catalog checks.

The final source audit additionally found the StorageDetails export-binding `DataGrid`, absent from the initial delta count. It now uses `CollectionView<StorageExportBinding>` with real RequirementId identity and Export/ValueType/Protection information, without owner, state, actions or an invented resource link. Including this newly reconciled dataset brings the total to **346**.

## Specialized native control contracts

The Flow data mapping combobox keeps its coordinated listbox, active descendant,
keyboard and free-expression behavior. The simple Value Binding literal input
keeps its explicit change event: committing an empty string differs from having
no literal, and InputBase suppresses an equal empty value. Both retain localized
labels and explicit reset actions. Read-only Storage/Service YAML textareas remain
selectable document viewers with accessible labels; they never expose clearing.
The architecture guard recognizes only these exact adapters and control shapes;
ordinary editable inputs in the same files still require the shared components.

## Tool creation/editor coverage split

The maintained shared-form case and schema-contract creation flow open `/tools/definitions/new`, now owned by `NewToolDefinition.razor`. Their mapping belongs to that creator. `ToolDefinitionEditor.razor` owns only `/tools/definitions/{Name}` and receives no inherited browser field claim from the creation case. Its field coverage is explicitly pending under #1122; the total remains six pending routed adapters (five added adapters and this retained editor). The creator route is partial rather than complete CRUD coverage. Case assertions and test counts are unchanged.

## Current local validation

The Release solution builds with zero warnings and errors. The full required Fast
lane passes 1,556 tests; Integration passes 1,537 with 28 environment-dependent
optional checks skipped. TypeScript, six JavaScript lifecycle checks, five port
fixture tests and seven catalog checks pass. The format gate passes on all 366
changed C#/Razor sources, including the two Source sequence regressions.
Current-head CI remains the final full-browser authority.

Two populated collection variants pass against product source `2c6a1adf`: English
desktop/dark and French mobile/light with real touch. The [capture gallery](evidence/1121/main-sync-1181/README.md)
records 12 unedited screenshots, source/image checksums and three retained layout
failures. It covers exact homonymous navigation/copy, native type-contract
disclosure, authorized scope/state/governance, required version selectors, shared
fields, modal Escape/focus restoration and zero document overflow. It does not
claim exhaustive populated evidence for every new dataset.

The new Tool Definition publication fields now participate in the existing
unsaved-change fingerprint; two regressions verify isolated edits, cancellation
and reversion. No new dirty-state framework is introduced for other editors.

A CI responsive Flow case exposed a pre-existing invalid fixture and stale-count
assertion. Its Input step does not emit `error`; the fixture now uses a real
Condition's `true`/`false` branches and asserts empty server validation before
opening the designer. Verification waits for the completed result panel before
checking the unchanged zero/one issue counts. Three local repetitions pass after
the correction; no test timeout, retry or expected count is relaxed.

Eight Flow Inspector variants additionally pass in English/French, light/dark,
on desktop/mobile after updating both consumers of the corrected transition ID.
No priority, clearing or issue-count assertion is relaxed.

The Source sequence handlers now reject queued Add commands while catalogs are
loading or the editor is read-only. Two controlled component regressions fail
before the correction and pass afterwards; all 712 Console component tests pass.
The browser waits for the requested refresh to complete using the local loading
revision, without changing persisted revisions, row counts, retries or timeouts.
All four Source composition cases pass with English then French sharing each
desktop/mobile host. [Source loading evidence](evidence/1121/source-sequence-loading/README.md)
distinguishes the reproduced handler defect from the original CI incident, whose
exact server cause was not established.

The historical CI working-set failure on API Resources is tracked separately by
#1219. Its updated revision `52305495` splits the unchanged inventory across bounded
test processes and preserves the 2,432 MiB failure ceiling. Its validation and
merge remain separate. The maintainer requested keeping that work separate;
no experimental memory-fixture correction is included in this UX promotion.
CI is not declared globally green while that gate remains failed.
