# Resource screen adoption on codex/ux-corrections

The maintainer authorized all resource screens, creation wizards, saved overviews with Identity panels, family-populated ResourceNavigation and header activation switches. One commit per screen; no pull request is requested. Existing field components, exact resource/owner/context coordinates, permissions, ETags and domain editors remain family-owned.

The table is a source inventory, not a claim of browser validation. Route aliases are grouped. Specialized run/history/immutable screens receive only capabilities supported by their domain. Collection and administration pages containing embedded resource editors are audited separately.

| Route-owning component | Routes | Adoption |
| --- | --- | --- |
| AgentEditor.razor | `/agents/new`, `/agents/{Name}` | Existing detail pilot; creation wizard pending |
| AgentRunner.razor | `/agents/{Name}/run`, `/runs/{RunId}` | Pending review and migration |
| ConnectedServiceDetails.razor | `/connected-services/{Name}` | Pending review and migration |
| ConnectedServiceEditor.razor | `/connected-services/new`, `/connected-services/{Name}/edit` | Pending review and migration |
| ConsoleEntryInteraction.razor | `/entry-interactions/{WorkspaceId:guid}/{EntryNamespace}/{EntryName}` | Pending review and migration |
| DataSourceEditor.razor | `/data-sources/new`, `/data-sources/{Name}` | Pending review and migration |
| DataSourceProfileEditor.razor | `/data-source-profiles/new`, `/data-source-profiles/{Name}` | Pending review and migration |
| EntryEditor.razor | `/entries/{Name}`, `/namespaces/{EntryNamespace}/entries/{Name}` | Pending review and migration |
| ExtensionDetails.razor | `/extensions/{RegistrationName}`, `/extensions/enrollment/{EnrollmentInstanceId:guid}` | Pending review and migration |
| FlowDesigner.razor | `/flows/{FlowId}/designer`, `/namespaces/{FlowNamespace}/flows/{FlowId}/designer` | Pending review and migration |
| FlowDetails.razor | `/flows/{FlowId}`, `/namespaces/{FlowNamespace}/flows/{FlowId}` | Pending review and migration |
| FlowOrchestrationEditor.razor | `/flows/{FlowId}/orchestration`, `/namespaces/{FlowNamespace}/flows/{FlowId}/orchestration` | Pending review and migration |
| FlowRunArtifactDetails.razor | `/artifacts/durable/{Id}` | Pending review and migration |
| FlowRunDetails.razor | `/flow-runs/{RunId}` | Pending review and migration |
| KnowledgeSourceDetails.razor | `/knowledge-sources/{Name}` | Pending review and migration |
| KnowledgeSourceEditor.razor | `/knowledge-sources/new`, `/knowledge-sources/{Name}/edit` | Pending review and migration |
| ModelDetails.razor | `/models/{Name}` | Pending review and migration |
| ModelProfileEditor.razor | `/modelprofiles/new`, `/modelprofiles/{Name}` | Pending review and migration |
| ModelProviderDetails.razor | `/modelproviders/new`, `/modelproviders/{Name}` | Pending review and migration |
| NamespacedAgentDetails.razor | `/namespaces/{AgentNamespace}/agents/{Name}` | Pending review and migration |
| NewFlow.razor | `/flows/new` | Pending review and migration |
| NewToolDefinition.razor | `/tools/definitions/new` | Pending review and migration |
| OrganizationMemberDetails.razor | `/settings/organization/members/{UserId:guid}` | Pending review and migration |
| PackComposer.razor | `/pack-projects/new` | Pending review and migration |
| PackProjectDetails.razor | `/pack-projects/{ProjectId:guid}` | Pending review and migration |
| ParameterEditor.razor | `/parameters/new`, `/parameters/{Name}` | Implemented; focused components and desktop/mobile browser checks pass. [Real captures](evidence/resource-screen-adoption/parameters/README.md). Full final validation remains pending. |
| ResourcePlanDetails.razor | `/resource-plans/{Id:guid}` | Pending review and migration |
| RuntimeProfileEditor.razor | `/runtimeprofiles/new`, `/runtimeprofiles/{Name}` | Implemented; 10 profile cases and 15 shared access-panel cases pass; TypeScript/catalog pass; browser page object maintained; browser checks pending |
| RuntimeWorkerDetails.razor | `/settings/runtime-workers/{WorkerId:guid}` | Pending review and migration |
| SchemaEditor.razor | `/schemas/new`, `/schemas/{Name}` | Pending review and migration |
| SecretEditor.razor | `/secrets/new`, `/secrets/{Name}` | Implemented; focused components and desktop/mobile browser checks pass. [Real captures](evidence/resource-screen-adoption/secrets/README.md). Full final validation remains pending. |
| SourceProviderDetails.razor | `/sourceproviders/new`, `/sourceproviders/{Name}` | Pending review and migration |
| SourceRegistries.razor | `/settings/source-registries`, `/settings/source-registries/new`, `/settings/source-registries/{Name}` | Pending review and migration |
| Sources.razor | `/settings/sources`, `/settings/sources/{Publisher}/{Name}` | Pending review and migration |
| StagedArtifactDetails.razor | `/artifacts/staged/{Id}` | Pending review and migration |
| StorageDetails.razor | `/storages/{Name}` | Pending review and migration |
| StorageEditor.razor | `/storages/new`, `/storages/{Name}/edit` | Pending review and migration |
| StorageTypeDetails.razor | `/storage-types/{Name}` | Pending review and migration |
| StorageTypeEditor.razor | `/storage-types/new`, `/storage-types/{Name}/edit`, `/storage-types/{Name}/versions/new` | Pending review and migration |
| TaskDetails.razor | `/tasks/{TaskId:guid}` | Pending review and migration |
| TaskFlowRunDetails.razor | `/tasks/{TaskId:guid}/flowruns/{RunId}` | Pending review and migration |
| ToolCategoryEditor.razor | `/tools/categories/new`, `/tools/categories/{Name}` | Pending review and migration |
| ToolDefinitionEditor.razor | `/tools/definitions/{Name}` | Pending review and migration |
| ToolDetails.razor | `/tools/{Name}` | Pending review and migration |
| ToolGovernanceAudit.razor | `/tool-governance/{Owner}/{RunId}` | Pending review and migration |
| ToolProviderEditor.razor | `/tools/providers/new`, `/tools/providers/{Name}` | Implemented; 8 focused switch/wizard/read-only/navigation cases pass; TypeScript/catalog pass; browser checks pending |
| ToolSetDetails.razor | `/tools/sets/{Name}` | Pending review and migration |
| TransformationFunctionEditor.razor | `/transformation-functions/{Name}` | Pending review and migration |
| TriggerDetails.razor | `/triggers/{Name}` | Pending review and migration |
| TriggerEditor.razor | `/triggers/new`, `/triggers/{Name}/edit` | Pending review and migration |
| VaultEditor.razor | `/vaults/new`, `/vaults/{Name}` | Layout/wizard browser checks pass at `70897500`; access-draft/managed follow-up passes the 31-case grant/access regression selection; fresh browser checks for that follow-up remain pending. [Real captures](evidence/resource-screen-adoption/vaults/README.md). Full final validation remains pending. |

## Embedded and delegated editors

- `DataSourceEditor` delegates dataset mode to `DataSourceDatasetEditor`; preserve the existing wizard and migrate its saved-resource composition.
- `ModelProviders`, `ModelProfiles`, `SourceRegistries`, `Sources`, `Extensions`, `ToolSets`, `Organization*`, `Workspaces` and Workplace routes require review for inline creation/detail forms and specialized business context.
- Specialized route screens above are reviewed individually; an immutable/history view is not an editable resource definition.

## Shared collection inventory

Parameter, Vault, Secret, Tool Provider and Runtime Profile saved configuration add shared frames: **93 shared frames + 258 active specialized collections = 351**, with 148 audited repeated-item sources before additional screen adoption. Historical evidence retains its recorded source commit.

## Final acceptance still required

Final verification includes exact resource/owner/context coordinates and homonyms, inherited and managed read-only behavior, draft retention across fragments and browser history, long identities, localized state semantics, keyboard/focus/Escape, touch disclosures, both themes, desktop/mobile bounds, current loading/pagination contracts and existing commands/ETags. Binary settings use switch presentation; multi-selection retains checkboxes. Unsupported publication or activation capabilities must not be invented.
