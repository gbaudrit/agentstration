# Resource-screen foundation reconciliation

Requested reference: `eb0632a5` on `codex/ux-corrections`. Source inspected for this initial inventory: `39fcb58446b29aa92a756c95f5a9c9e09cc0a544`.

## Reference availability

The published branch was fast-forwarded without overwriting local work to `9a192001`, then synchronized with the parallel formatting commit `7e4a53f2`, which contains `eb0632a5` and the subsequent integration/culture correction commits. The current authoring guide, component contracts, Console adapter, definition form and protected Agent reference have been read. PR #1238 tracks the same branch against main; its existing UX follow-up links are #1234 and #1236.

Agent source, styles and translations are protected from this migration. The previous inventory is being reconciled against the new standard per screen; previous passes do not establish new-foundation compliance.

## Reconciled previous scope

The previous adoption matrix contains **51 route-owning components**, grouping aliases and creation/detail routes: **31 previously treated** and **20 previously remaining**. These are historical adoption statuses, not compliance with the requested new foundation. Agent is the protected reference. The other components require renewed review, including previously treated families. Collection routes keep their collection layout; creation retains its own layout and `CreationWizard`. Embedded editors and specialized operational views require contract inspection rather than automatic exclusion or fabricated resource capabilities.

The classification below identifies inspection boundaries; specialized exceptions are not approved exclusions. Exact composition and justified omissions are being reconciled with the current guide in this checkout.

| Component | Previous pass | Inspection boundary | Current wizard present |
| --- | --- | --- | --- |
| `AgentEditor.razor` | Previously treated; review again | Protected Agent reference | Yes |
| `AgentRunner.razor` | Previously treated; review again | Specialized operational/identity view; inspect supported definition, access and export capabilities | No |
| `ConnectedServiceDetails.razor` | Previously treated; review again | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `ConnectedServiceEditor.razor` | Previously treated; review again | Delegated definition/editor; coordinate with owning detail and preserve creation wizard | Yes |
| `ConsoleEntryInteraction.razor` | Previously remaining | Specialized operational/identity view; inspect supported definition, access and export capabilities | No |
| `DataSourceEditor.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `DataSourceProfileEditor.razor` | Previously treated; review again | Persisted resource detail; adopt the Console template and shared definition contract | Yes |
| `EntryEditor.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `ExtensionDetails.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `FlowDesigner.razor` | Previously remaining | Specialized operational/identity view; inspect supported definition, access and export capabilities | No |
| `FlowDetails.razor` | Previously treated; review again | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `FlowOrchestrationEditor.razor` | Previously treated; review again | Delegated definition/editor; coordinate with owning detail and preserve creation wizard | No |
| `FlowRunArtifactDetails.razor` | Previously treated; review again | Specialized operational/identity view; inspect supported definition, access and export capabilities | No |
| `FlowRunDetails.razor` | Previously treated; review again | Specialized operational/identity view; inspect supported definition, access and export capabilities | No |
| `KnowledgeSourceDetails.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `KnowledgeSourceEditor.razor` | Previously remaining | Delegated definition/editor; coordinate with owning detail and preserve creation wizard | No |
| `ModelDetails.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `ModelProfileEditor.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `ModelProviderDetails.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `NamespacedAgentDetails.razor` | Previously treated; review again | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `NewFlow.razor` | Previously treated; review again | Creation only; preserve/create CreationWizard | Yes |
| `NewToolDefinition.razor` | Previously treated; review again | Creation only; preserve/create CreationWizard | Yes |
| `OrganizationMemberDetails.razor` | Previously treated; review again | Specialized operational/identity view; inspect supported definition, access and export capabilities | No |
| `PackComposer.razor` | Previously remaining | Creation only; preserve/create CreationWizard | No |
| `PackProjectDetails.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `ParameterEditor.razor` | New foundation migrated; local verification recorded below | Persisted resource detail; adopt the Console template and shared definition contract | Yes |
| `ResourcePlanDetails.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `RuntimeProfileEditor.razor` | New foundation migrated; local verification recorded below | Persisted resource detail; adopt the Console template and shared definition contract | Yes |
| `RuntimeWorkerDetails.razor` | Previously treated; review again | Specialized operational/identity view; inspect supported definition, access and export capabilities | No |
| `SchemaEditor.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `SecretEditor.razor` | New foundation migrated; local verification recorded below | Persisted resource detail; adopt the Console template and shared definition contract | Yes |
| `SourceProviderDetails.razor` | New foundation migrated; local verification recorded below | Persisted resource detail; adopt the Console template and shared definition contract | Yes |
| `SourceRegistries.razor` | Previously remaining | Mixed collection/detail; keep collection template and inspect detail adapter | No |
| `Sources.razor` | Previously remaining | Mixed collection/detail; keep collection template and inspect detail adapter | No |
| `StagedArtifactDetails.razor` | Previously treated; review again | Specialized operational/identity view; inspect supported definition, access and export capabilities | No |
| `StorageDetails.razor` | Previously treated; review again | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `StorageEditor.razor` | Previously treated; review again | Delegated definition/editor; coordinate with owning detail and preserve creation wizard | Yes |
| `StorageTypeDetails.razor` | Previously treated; review again | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `StorageTypeEditor.razor` | Previously treated; review again | Delegated definition/editor; coordinate with owning detail and preserve creation wizard | Yes |
| `TaskDetails.razor` | Previously remaining | Specialized operational/identity view; inspect supported definition, access and export capabilities | No |
| `TaskFlowRunDetails.razor` | Previously treated; review again | Specialized operational/identity view; inspect supported definition, access and export capabilities | No |
| `ToolCategoryEditor.razor` | New foundation migrated; local verification recorded below | Persisted resource detail; adopt the Console template and shared definition contract | Yes |
| `ToolDefinitionEditor.razor` | Previously treated; review again | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `ToolDetails.razor` | Previously treated; review again | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `ToolGovernanceAudit.razor` | Previously treated; review again | Specialized operational/identity view; inspect supported definition, access and export capabilities | No |
| `ToolProviderEditor.razor` | Previously treated; review again | Persisted resource detail; adopt the Console template and shared definition contract | Yes |
| `ToolSetDetails.razor` | Previously treated; review again | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `TransformationFunctionEditor.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `TriggerDetails.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `TriggerEditor.razor` | Previously remaining | Delegated definition/editor; coordinate with owning detail and preserve creation wizard | No |
| `VaultEditor.razor` | New foundation migrated; local verification recorded below | Persisted resource detail; adopt the Console template and shared definition contract | Yes |

## Embedded and delegated surfaces

The previous inventory also explicitly identifies `DataSourceDatasetEditor`, `ModelProviders`, `ModelProfiles`, `Sources`, `SourceRegistries`, `Extensions`, `ToolSets`, organization and workspace editors as review boundaries. An outer collection must not acquire a resource-detail template solely because it contains an editor. Shared/delegated forms must receive the owning detail's section state and presentation; no second resource shell or duplicate form is implied by this inventory.

## Migration checks to record per screen

- Exact resource namespace, owner, UID and administrative context; family-owned commands, ETags and confirmations.
- Standard navigation and the actual supported business sections, authorized independent Access, final YAML when the resource contract supports it.
- Saved Overview indicators before Identity; labels inside Identity; family enrichments in AdditionalPanels.
- ResourceDefinitionForm with the same ResourceSectionState and ResourceSectionPresentation as its owning ConsoleResourceDetailsTemplate; declared Fields and first-error navigation/focus.
- Model-owned rules, specialized validators and original binding expressions; read-only resource state separated from independent execution and access authorization.
- Visited invalid forms, raw YAML and policy drafts retained across fragments and refresh, with explicit discard rather than silent replacement.
- Existing EN/FR components and theme spacing; targeted checks and desktop/mobile smoke for modified journeys. Shared-component changes additionally require Agent regression verification without editing its reference implementation.
- One commit per screen, exact validation evidence and justified capability omissions. New reusable journeys retain the AGENTS.md UX-validation/merge gate; existing regressions are maintained.

No implementation or test result on the requested foundation is inferred from this initial inventory. Per-screen completion and verification will be recorded as migrations are committed.

## Completed new-foundation migrations

### ParameterEditor

- Standard Console template and common retained definition form/state/presentations; standard Identity and Configuration, family descendant-use section, independent resource Access, final saved YAML export.
- Creation retains its wizard. Labels are inside Identity and read-only because the family API does not write metadata; the shared component preserves the family name policy without changing Agent defaults.
- Draft-preserving refresh, original/newer ETag handling and explicit discard, shared loading/retry/read-only states, and replacement-identity clearing.
- 47 targeted cases pass; TypeScript passes; Parameter desktop/French touch probes, both maintained naming cases and both maintained Agent regression cases pass. [Real evidence and exact source hashes](evidence/resource-screen-foundation/parameters/README.md).
- Agent's protected files are unchanged. No CI failure is inspected or remediated as part of this migration. Remaining previously treated and pending screens still require their individual new-foundation pass; the historical 31/20 count is not a new-foundation completion count.

### VaultEditor

- Standard Console template and retained definition form with shared state/presentations, standard Identity then Configuration, authorized independent Access, master-key commands and final saved YAML. Actual health metric above saved Identity; labels stay within Identity.
- Creation retains its wizard and family-authorized inventory. Provider enrichments use AdditionalPanels. Description/labels stay read-only because their mutation is unsupported; no activation or namespace capability is invented.
- Refresh preserves invalid drafts and original ETags, buffers newer snapshots for explicit discard, and clears private content on changed identity or lost access. Initialization retains its existing command/health distinction and confirmation; the key-file path warning wraps on mobile.
- 57 targeted cases, TypeScript, both final Vault desktop/touch probes, the maintained descendant-grants journey and both maintained Agent regression cases pass. [Real captures and exact source hashes](evidence/resource-screen-foundation/vaults/README.md).
- Protected Agent product files remain unchanged. Synchronization occurs before and after the screen commit; CI failures are not handled.

### SecretEditor

- Standard Console template, actual value-status metric above saved Identity/labels, retained definition form with shared state/presentations, Identity before Configuration, family descendant-use section, independent Access and final saved metadata YAML. Creation retains its wizard and name/key derivation.
- Refresh preserves invalid metadata, its EditContext and original ETag; explicit discard adopts a newer snapshot. Write-only replacement remains an independent draft/command and survives metadata save/discard and refresh. The adapter's single combined guard protects it without displaying the metadata save bar. Replacement identity/lost access clears private pending content.
- Namespace stays immutable; labels are read-only because the existing family API has no metadata mutation contract. Actual saved references preserve namespace/owner/context; no value retrieval/export or activation capability is introduced.
- TypeScript, both Secret desktop/touch probes, the maintained descendant-grants journey and both Agent regression cases pass. [Real captures, source hashes and focused checks](evidence/resource-screen-foundation/secrets/README.md).
- Agent product files remain unchanged. One screen commit, synchronized before and after; CI failures remain outside this work.

### RuntimeProfileEditor

- Standard Console template and retained form/state/presentations, actual deployment-count metric before saved Identity/labels, Identity runtime-type enrichments in AdditionalPanels, Configuration execution settings, advanced JSON options, independent Access and final saved YAML.
- Creation retains its wizard and editable namespace through an opt-in shared Identity capability; saved namespaces remain immutable. Description/labels stay read-only because their writes are unsupported. The family remains Instance-owned; no activation state or owner is invented.
- Specialized JSON parser/store, enum bindings, notifications, usage-dependent deletion and ETags remain intact. Refresh retains invalid JSON/messages and original ETag until explicit discard; identity replacement/access loss clears private content.
- All 46 focused cases, TypeScript, both desktop/touch Runtime Profile probes, the maintained model-administration lifecycle and both maintained Agent regression cases pass. [Real captures and exact source hashes](evidence/resource-screen-foundation/runtime-profiles/README.md).
- Protected Agent product files remain unchanged. Synchronization precedes/follows the screen commit; CI failures remain outside this work.

### SourceProviderDetails

- Standard Console template, actual observed-capability metric above saved Identity/labels, retained common form/state/presentations, Identity before Configuration, existing observed capability/usages, independently authorized Access and final saved YAML. Creation retains its wizard, suggestions and safe return handling.
- Exact extension owner/namespace/name key, saved-contribution fallback, namespace normalization/deferred binding, family eligibility, deletion restrictions and ETags are retained. Description/labels stay read-only because their writes are unsupported. No activation or YAML mutation capability is introduced.
- Refresh preserves invalid drafts/messages and original ETags until explicit discard; replacement/access loss clears private content. A missing persisted owner now produces common failed-load feedback rather than assuming Instance; generated links already carry the exact owner.
- All 48 focused cases, TypeScript, both final Source Provider desktop/touch probes, the maintained local Git creation case, both Runtime Profile normalized-namespace probes and both maintained Agent regression cases pass. [Real captures and exact hashes](evidence/resource-screen-foundation/source-providers/README.md).
- Protected Agent product files remain unchanged. #1236 records the transverse deferred composition scope and stays open/blocked. One screen commit with synchronization before/after; CI failures are not handled.

### ToolCategoryEditor

- Standard Console template, actual saved member count above saved Identity/labels, retained common form/state/presentations, Identity before membership Configuration, independently authorized Access and final saved YAML. Creation keeps its wizard, namespace/scope and existing limits.
- A family editor model retains validation, eligibility, raw relative/missing references and properties projection. Grouped provider/member presentation, exact homonym-safe IDs/links and bounded-catalog fallback remain intact. Membership stays checkboxes under its existing selection contract; the Workspace-only category has no activation or descendant policy target. Labels are read-only because metadata writes are unsupported.
- Refresh retains invalid metadata and membership changes made while its request is in flight; explicit discard adopts the refreshed family catalog/member bundle and ETag. Deletion invalidates the removed snapshot before leaving, releasing the combined guard. Scope validation remains inside the common field, including unavailable targets.
- All 49 focused cases, both final Category desktop/touch probes and both maintained Agent regression cases pass. [Real captures, geometry and exact hashes](evidence/resource-screen-foundation/tool-categories/README.md).
- Protected Agent product files remain unchanged. Shared empty feedback no longer creates a phantom gap. Deferred UX follow-up remains open/blocked; CI failures are not handled.
