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
| `DataSourceProfileEditor.razor` | New foundation migrated; local verification recorded below | Historical profile resource; preserve definition-only API and publication/Flow contracts | Yes |
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
| `NewFlow.razor` | Creation review complete; local verification below | Dedicated wizard, exact participant/manager references and draft-only creation | Yes |
| `NewToolDefinition.razor` | Creation review complete; local verification below | Dedicated Flow/Relay wizard, exact source contracts and draft-only commands | Yes |
| `OrganizationMemberDetails.razor` | Previously treated; review again | Specialized operational/identity view; inspect supported definition, access and export capabilities | No |
| `PackComposer.razor` | Previously remaining | Creation only; preserve/create CreationWizard | No |
| `PackProjectDetails.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `ParameterEditor.razor` | New foundation migrated; local verification recorded below | Persisted resource detail; adopt the Console template and shared definition contract | Yes |
| `ResourcePlanDetails.razor` | Previously remaining | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `RuntimeProfileEditor.razor` | New foundation migrated; local verification recorded below | Persisted resource detail; adopt the Console template and shared definition contract | Yes |
| `RuntimeWorkerDetails.razor` | Reviewed operational inspection; shared state adoption and verification below | Worker/process and bounded assignment projections, not a persisted Resource definition | Not applicable |
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
| `TaskFlowRunDetails.razor` | Reviewed operational execution; shared states and verification below | Task-bound FlowRun record, not an editable Resource definition | Not applicable |
| `ToolCategoryEditor.razor` | New foundation migrated; local verification recorded below | Persisted resource detail; adopt the Console template and shared definition contract | Yes |
| `ToolDefinitionEditor.razor` | Previously treated; review again | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `ToolDetails.razor` | New foundation migrated; local verification recorded below | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `ToolGovernanceAudit.razor` | Reviewed specialized collection; privacy fix and verification below | Execution audit event collection, not a persisted Resource definition | Not applicable |
| `ToolProviderEditor.razor` | New foundation migrated; local verification recorded below | Persisted resource detail; adopt the Console template and shared definition contract | Yes |
| `ToolSetDetails.razor` | New foundation migrated; local verification recorded below | Persisted resource detail; adopt the Console template and shared definition contract | No |
| `TransformationFunctionEditor.razor` | New foundation migrated; local verification recorded below | Persisted resource detail with immutable native versions | Yes |
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

### ToolProviderEditor

- Standard Console template and retained common form/state/presentations, actual discovery metrics above saved Identity/labels, provider-type AdditionalPanels enrichment, Identity then connection Configuration, tools, independently authorized Access and final saved YAML. Creation retains its wizard and its actual API capabilities.
- Existing model rules/parsers, exact/relative extension references, header activation draft semantics, command restrictions and ETags are preserved. Qualified provider writes/tests/discovery now retain the loaded namespace/owner instead of relying on an unqualified name. Refresh buffers newer definitions until explicit discard and preserves invalid/in-flight edits; identity/access changes clear private rendered content.
- 14 component cases, one client case, one API homonym/concurrency case, TypeScript, both final desktop/touch probes and both maintained governed-inventory cases pass. [Real captures, exact hashes and validation chronology](evidence/resource-screen-foundation/tool-providers/README.md).
- Protected Agent product files remain unchanged. No shared presentation component change; no CI failure is handled. Seven adapters now have a new-foundation migration; the other 43 non-Agent route-owning components still require review/migration.

### ToolSetDetails

- Standard Console inspection template, actual metrics before saved Identity/labels, one retained locked definition form/state/presentations, Identity then Configuration, immutable publications, independently authorized Access and final saved YAML. No unsupported creation, definition-write or activation command is invented; the collection/API creation paths are preserved.
- Exact owner/namespace/UID, published member provenance, ETags, confirmations, backend guards and bounded history calls remain intact. Recoverable refresh retains policy drafts/content; lost access or replaced identity clears private content. Full copyable publication hashes fix mobile overflow.
- 19 targeted component cases and both final desktop/touch probes pass; both maintained governed-inventory cases pass before the final hash-wrapping follow-up. [Real captures, exact hashes and validation limits](evidence/resource-screen-foundation/tool-sets/README.md).
- Existing ToolSetDeletionGuard rejects Instance/Tenant deletion despite broader family ownership support. The real probe verifies the rejection and retained draft; successful Workspace deletion is covered at the component command contract, not claimed as browser-tested. No backend scope rule or CI failure is handled here.
- Parallel list-template updates are preserved. After synchronization, 33 Tool Set/Tool Provider component cases, TypeScript and both maintained Agent desktop/touch cases pass; final Tool Set desktop/touch probes pass on the updated shared components. Protected Agent files remain unchanged. Eight adapters now have a new-foundation migration; 42 other non-Agent route-owning components still require their individual review/migration.

### ToolDetails

- Standard Console template, real observation metric before saved Identity/labels, locked common definition form/state/presentations, Identity then Configuration, retained execution, independently authorized Access and final saved YAML. Existing schema, provenance, diagnostics, receipt/artifact links and Create Tool Definition destination remain intact. Catalog metadata has no unsupported Save or creation wizard on this route.
- Immediate activation preserves the original ETag and execution input; write and execute permissions remain independent. Refresh retains invalid raw JSON and its accepted schema. A changed contract requires explicit input reset before submission, preserving the pending parser buffer until that action. Replacement/access loss clears private content; recoverable failure preserves policy drafts. Missing observation remains neutral/absent rather than invented unavailability.
- All 17 targeted cases, both final desktop/touch probes with real local MCP execution and both maintained governed-inventory cases pass. [Real captures, exact hashes and synchronization scope](evidence/resource-screen-foundation/tools/README.md). A test-only parallel update was preserved and the 17 cases pass again after synchronization.
- No shared component or protected Agent product file changed; no CI failure is handled. Nine adapters now have a new-foundation migration; 41 other non-Agent route-owning components still require their individual review/migration.

### ToolGovernanceAudit

- Reviewed the actual audit record/page contracts: this is a bounded collection of execution events, not a persisted resource definition. Retain its specialized chronological cards, filters, nested evaluations and operational shared shell. No fabricated Identity editor, metadata labels, YAML, resource Access or creation wizard. Execution ownership is business context rather than resource ownership.
- Corrected access-loss clearing during continuation; recoverable failures retain records and retry the actual cursor. All ten focused cases pass, including three red-before/fixed-after privacy cases. Desktop/touch smoke and real application captures cover navigation, filters, missing-run feedback, Escape/focus and width. Populated-record privacy/retry are component-tested; the browser probe does not claim populated audit coverage. [Evidence and justified composition](evidence/resource-screen-foundation/tool-governance-audit/README.md).
- Parallel category-list/test changes were synchronized and preserved. No shared/protected Agent product file changes; no CI failure handled. Nine adapters are migrated, one specialized collection is explicitly reviewed, Agent remains protected, and 40 other route-owning components still require their individual pass.

### TransformationFunctionEditor

- Standard Console template, actual version count before saved Identity/labels, retained common form/state/presentations and root Fields, Identity then Configuration, independent Access and final saved YAML. Creation uses CreationWizard with standard identity and labels; incomplete steps stay navigable. The pre-existing fixed owner/query namespace are retained. No unsupported YAML mutation or new scope authority is invented.
- Native kind/implementation selection, graphical contract inspection and backend version immutability remain intact. Saved overview uses complete wrapping inspection values instead of disabled schema forms. Metadata/activation writes preserve frozen native-contract evidence and exact kind references; activation remains draft-only until Save. Refresh freezes invalid forms/labels, policy and original ETag, including input entered during the request; newer snapshots require explicit discard.
- All 14 targeted cases and both final real creation/inspection desktop/touch probes pass. Both maintained Agent cases pass in the original campaign; subsequent family-only follow-ups change no shared product source. Protected Agent source/styles/translations are unchanged. [Captures, exact source/host hashes and verification scope](evidence/resource-screen-foundation/transformation-functions/README.md).
- No CI failure is inspected or remediated. Ten adapters are migrated, one specialized collection is explicitly reviewed, Agent remains protected, and 39 other route-owning components still require their individual pass.

### RuntimeWorkerDetails

- Reviewed RuntimeWorkerDetailsResponse/assignment contracts: enrolled process inspection rather than a persisted Resource. Retain the shared operational shell, Overview/Assignments, real metrics above Identity, Configuration and physical-attempt history. No fabricated resource namespace/owner, definition form, generic policy, YAML or creation wizard. Presence is observed rather than activation.
- Adopted shared typed loading/refresh/error/retry states and one section state. Recoverable refresh retains inspection/selection; access loss, missing/replaced identity and superseded reads cannot retain private data. Assignment links retain exact execution scope and independent navigation context; attempt disclosure is keyed to its actual assignment. Existing server history remains bounded to 100 recent assignments.
- All 11 focused cases, both real desktop/touch inspection probes and both maintained Runtime Worker browser cases pass, including real assignment/loss-history/authorization checks. [Justification, captures and exact tested source/host hashes](evidence/resource-screen-foundation/runtime-workers/README.md).
- No shared/protected Agent product file changes and no CI failure handled. Ten resource adapters are migrated, two specialized surfaces are explicitly reviewed/adapted, Agent remains protected, and 38 other route-owning components still require their individual pass.

Transformation Function refresh follow-up: an unchanged ETag/generation no longer raises a false newer-definition notice. The previously failing partial-label/policy case now passes; all 14 focused cases and both updated real desktop/touch probes pass, including unchanged refresh followed by a genuine concurrent update. Draft/ETag retention and explicit discard are unchanged. Latest captures/source proof replace the prior campaign in the family evidence, with its initial proof retained separately. No CI failure handled; adoption counts are unchanged.

### DataSourceProfileEditor

- Standard Console template and retained definition form with the same state/presentations/root Fields; actual active-publication metric before saved Identity/labels; Identity, Configuration, Contracts/Policies, Revisions, independent Access and final YAML. Creation retains CreationWizard and create-then-publish/retry semantics. Labels remain read-only because the API writes only definition properties. Existing fixed identity, description limit, exact/relative pinned Flow references, ETags and publication commands remain intact.
- Invalid JSON/YAML and policy state survive navigation/refresh; input typed during a pending read is retained. Changed snapshots/history require explicit discard and unchanged snapshots do not raise false conflicts. Publication-version input remains independent. Access loss, replaced identity and lost read permission after publication clear private content; successful deletion releases the combined guard before leaving.
- Optional common YAML input selector retains `profile-yaml` without changing default output. Existing governed-inventory assertions now verify mandatory locked Definition inspection and ordinary Identity editing. All 27 focused cases, TypeScript, actual Release host builds, both real profile desktop/touch probes, both maintained governed-inventory cases and both Agent cases pass. Protected Agent files remain byte-identical. [Captures, exact source/host hashes and justified capabilities](evidence/resource-screen-foundation/data-source-profiles/README.md).
- No CI failure is inspected or remediated. Eleven resource adapters are migrated, two specialized surfaces are explicitly reviewed/adapted, Agent remains protected, and 37 other route-owning components still require their individual pass.

### NewFlow

- Creation retains its specific CreationWizard, standard identity fields, free incomplete-step navigation, raw inputs, templates, all five orchestration strategies, exact selected owner/default namespace and draft-only destinations. Common activation switch and binary switch presentation; multi-selection remains checkboxes. Interactivity and resources/write guards lock both presentation and commands; known denied users receive the common notice.
- Fixed qualified participant/manager identity loss and homonym selection. Metadata editing preserves complete immutable-reference evidence and relative coordinates. The existing domain rule requiring unique participant IDs is unchanged; a localized explanation and explicit removal/reselection disambiguate homonyms without inventing aliases. Catalog/preview resolution never guesses another Tenant/Workspace, adds no row queries and never rewrites a saved relative reference.
- Fixed incomplete Handoff/Magentic previews while keeping final validation. All 25 focused cases, TypeScript, actual Release host builds, both final desktop/touch probes, both Agent cases and the maintained Handoff Flow/Entry case pass. Protected Agent files remain unchanged. [Real captures, exact hashes and verified scope](evidence/resource-screen-foundation/new-flow/README.md).
- Parallel Tool Set and Tool Definition list work is preserved. No CI failure handled. Eleven Resource adapters, two specialized surfaces and one creation are reconciled, Agent remains protected, and **36 other route-owning components** still require their individual pass. FlowDetails and delegated FlowOrchestrationEditor remain pending for the common persisted layout/form; the shared reference correction does not count them as migrated.

### NewToolDefinition

- Creation retains its dedicated CreationWizard, standard identity fields, freely reachable incomplete steps and specialized Flow/Relay configuration, binding validation and dialogs. Exact namespace/owner, published Flow version/schema, pinned Tool UID/generation and create-only semantics remain unchanged. No unsupported metadata or publication capability is introduced.
- Interactivity/resources-write guards lock the native form, wizard, source preparation and final commands. A known reader gets the shared permission notice; scope feedback and context-change guards remain distinct. The administrator test fixture declares its permissions explicitly.
- All 33 focused creation/definition cases, actual Release host builds and both real desktop/touch probes pass. Both implementations create in the exact namespace/owner/context and leave publication history empty. [Real captures and exact source/host hashes](evidence/resource-screen-foundation/new-tool-definition/README.md). No shared/protected Agent product files change; no CI failure handled.
- Eleven Resource adapters, two specialized surfaces and two creations are reconciled, Agent remains protected, and **35 other route-owning components** require their individual pass. Persisted ToolDefinitionEditor and delegated ToolRelayEditor remain pending; preserving their creation fragments does not count their detail-layout migration as complete.

Tool Definition creation feedback follow-up: final-attempt identity errors now track the current model completeness and disappear when corrected; source/command failures remain separate. Both EN/FR assertions failed before the fix and pass afterward; all 33 focused cases and both actual desktop/touch probes pass again, including no stale alert on Review for either implementation. Captures/source proof are refreshed, initial proof retained. No shared product or protected Agent files change; adoption counts and deferred-scenario gate remain unchanged.

### TaskFlowRunDetails

- Reviewed FlowRun and WorkOperationsQueryService: Task-bound execution inspection with no editable Resource metadata, definition/policy/YAML contract. Keep specialized shared operational layout, Overview/Execution, topology/IO/step inspector and independently authorized Work query. No fabricated resource namespace, owner badge, labels, Definition/YAML/Access or wizard. Actual status/historical-Flow metrics precede Identity; associated Flow namespace and execution context retain their real meaning.
- Adopted shared typed loading/refresh/error/retry and one section state. Recoverable failures keep inspection/selection; access loss/missing relations clear private data. Wrong Task/run/scope and late/disposed responses cannot replace it. Parent links retain navigation context; business/query/authorization semantics are unchanged.
- All 11 focused cases, actual Release host builds and both real desktop/touch probes pass, including actual linked Task/Run creation/inspection, successful refresh, wrong-Task not-found, strict width and touch Escape/focus. Fault-injected retry/privacy are component-tested, not claimed as browser outages. [Justification, captures and exact hashes](evidence/resource-screen-foundation/task-flow-runs/README.md).
- No shared/protected Agent files change and no CI failure handled. Eleven Resource adapters, three specialized surfaces and two creations are reconciled, Agent remains protected, and **34 other route-owning components** require their individual pass. Standalone FlowRunDetails remains pending; this Task-bound route does not count it as migrated.
