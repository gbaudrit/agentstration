# Resource collection navigation adoption

Branch: `feat/resource-list-navigation`, created from `codex/ux-corrections` at `7ba891ad`.

The screen supplies a real list destination and localized label; the shared headers render the final list-icon link. Explicit execution coordinates and target queries remain family-owned. Missing target context is filled by the existing Console navigation context. Duplicate list Back/All/plain Cancel links are removed; parent links and real cancellation commands remain. No route inference or new loading API is introduced.

## Surface reconciliation

The foundation inventory contains 52 route-owning components, grouping aliases and delegated editors. The table records each boundary; additional delegated/variant components are recorded below. Collection and embedded editors keep their own templates. Agent receives only the bounded navigation wiring, with no style, form or translation migration.

| Component | Treatment / destination |
| --- | --- |
| `AgentEditor` | Shared header: `/agents`. Existing exact URL helper retained. |
| `AgentRunner` | Only the existing missing-Agent list fallback uses the common link; loaded Agent retains its exact parent link. |
| `ConnectedServiceDetails` | Shared header: `/connected-services` through the existing context-preserving URL helper. |
| `ConnectedServiceEditor` | Shared header: `/connected-services`. Existing exact URL helper retained. |
| `ConsoleEntryInteraction` | Shared header: `/conversations`. Existing exact URL helper retained. |
| `DataSourceEditor` | Shared header: `/data-sources`. Existing exact URL helper retained. |
| `DataSourceProfileEditor` | Shared header: `/data-source-profiles` through the existing context-preserving URL helper. |
| `EntryEditor` | Shared header: `/entries` through the existing context-preserving URL helper. |
| `ExtensionDetails` | Shared header: `/extensions` through the existing context-preserving URL helper. |
| `Extensions` | Inline creator belongs to the collection; no self-return link. |
| `FlowDesigner` | Dedicated workshop uses the common link directly; existing `FlowListUrl`, parent draft link and hierarchy preserved. |
| `FlowDetails` | Existing pilot `CollectionHref` retained; shared header now positions it last. |
| `FlowOrchestrationEditor` | Embedded definition; standalone alias redirects to FlowDetails, whose header owns the list link. |
| `FlowRunArtifactDetails` | Shared header: `/artifacts` through the existing exact URL helper. |
| `FlowRunDetails` | Shared header: `@RunCollectionUrl`. Existing exact URL helper retained. |
| `KnowledgeSourceDetails` | Shared header: `/knowledge-sources`. Existing exact URL helper retained. |
| `KnowledgeSourceEditor` | Shared header: `/knowledge-sources`. Existing exact URL helper retained. |
| `ModelDetails` | Shared header: `/modelproviders` through the existing context-preserving URL helper. |
| `ModelProfileEditor` | Shared header: `/modelprofiles` through the existing context-preserving URL helper. |
| `ModelProviderDetails` | Shared header: `/modelproviders` through the existing context-preserving URL helper. |
| `NamespacedAgentDetails` | Shared header: `/agents`. Existing exact URL helper retained. |
| `NewFlow` | Shared header: `/flows` through the existing exact URL helper. |
| `NewToolDefinition` | Shared header: `/tools/definitions` through the existing exact URL helper. |
| `OrganizationMemberDetails` | Shared header: `/settings/organization/members` through the existing context-preserving URL helper. |
| `PackComposer` | Shared header: `/packs` through the existing context-preserving URL helper. |
| `PackProjectDetails` | Shared header: `/packs` through the existing context-preserving URL helper. |
| `ParameterEditor` | Shared header: `/parameters` through the existing context-preserving URL helper. |
| `ResourcePlanDetails` | Shared header: `/resource-plans` through the existing context-preserving URL helper. |
| `RuntimeProfileEditor` | Shared header: `/runtimeprofiles` through the existing context-preserving URL helper. |
| `RuntimeWorkerDetails` | Shared header: `/settings/runtime-workers` through the existing exact URL helper. |
| `SchemaEditor` | Shared header: `/schemas`. Existing exact URL helper retained. |
| `SecretEditor` | Shared header: `/secrets` through the existing context-preserving URL helper. |
| `SourceProviderDetails` | Shared header: `/sourceproviders` through the existing context-preserving URL helper. |
| `SourceRegistries` | Shared header: `/settings/source-registries` through the existing context-preserving URL helper. |
| `Sources` | Shared header: `@SourceCollectionUrl`. Existing exact URL helper retained. |
| `StagedArtifactDetails` | Shared header: `/artifacts` through the existing exact URL helper. |
| `StorageDetails` | Shared header: `/storages` through the existing context-preserving URL helper. |
| `StorageEditor` | Shared header: `/storages` through the existing context-preserving URL helper. |
| `StorageTypeDetails` | Shared header: `/storage-types` through the existing context-preserving URL helper. |
| `StorageTypeEditor` | Shared header: `/storage-types` through the existing context-preserving URL helper. |
| `TaskDetails` | Shared header: `/tasks` through the existing exact URL helper. |
| `TaskFlowRunDetails` | Task-bound inspection: retain Back to Task. No independent Task-run collection exists. |
| `ToolCategoryEditor` | Shared header: `/tools/categories` through the existing context-preserving URL helper. |
| `ToolDefinitionEditor` | Shared header: `/tools/definitions` through the existing context-preserving URL helper. |
| `ToolDetails` | Shared header: `/tools` through the existing exact URL helper. |
| `ToolGovernanceAudit` | Run-scoped audit collection: retain Back to Run. Do not invent an audit-list destination. |
| `ToolProviderEditor` | Shared header: `/tools/providers` through the existing context-preserving URL helper. |
| `ToolSetDetails` | Shared header: `/tools/sets` through the existing context-preserving URL helper. |
| `TransformationFunctionEditor` | Shared header: `/transformation-functions` through the existing context-preserving URL helper. |
| `TriggerDetails` | Existing pilot `CollectionHref` retained; shared header now positions it last. |
| `TriggerEditor` | Shared header: `/triggers` through the existing context-preserving URL helper. |
| `VaultEditor` | Shared header: `/vaults` through the existing context-preserving URL helper. |

Additional variant/delegated boundaries: `DataSourceDatasetEditor` declares `/data-sources` when standalone; `ToolRelayEditor` shares its owning Tools destination; Web `FlowDesigner` uses the same component after its overflow. Embedded Knowledge Source, Storage and Connected Service editors rely on their owner and never duplicate its header. `ModelDetails` returns to the actual Provider collection while keeping Open Provider. No Models collection is invented. Collection/settings/dashboard/discovery routes are not resource detail screens and receive no link to themselves.

## Verification

- Release builds: shared/Console component tests, Web, Governed Inventory, Workplace, four extension hosts and Runtime Worker; zero warnings/errors.
- Targeted Web component tests: **41 passed**, including the new collection-navigation contract.
- Targeted Console tests: **117 passed** (template, Agent, Flow and conversation regressions). The existing conversation test now supplies the browser navigation module URL, and asserts the same exact destination on the shared link.
- TypeScript: `npm exec tsc -- --noEmit` passed.
- Dedicated native probes: **2 passed**, English desktop/dark and French touch/light, including actual navigation, invalid draft cancellation/Escape/focus, explicit discard, missing-resource return, 32/44 px targets and no horizontal overflow. [Eight real captures and source hashes](evidence/resource-collection-navigation/README.md).
- Existing Flow/Entry publication journey: **1 passed**. Agent desktop/mobile regression: **2 passed** (English dark / French light). The five passing browser cases come from these focused runs; a full browser campaign is outside scope. The source and host assembly hashes are frozen during browser runs; no product assemblies are rebuilt during a journey.

The existing Agent journey is reconciled with the parent branch's header-owned display name and initially collapsed Configuration panel: assert the saved title in the header, expand Configuration before measuring its table, and retain saved-versus-draft, validation, language, layout and refresh assertions. No Agent product style, form or translation is changed by this reconciliation.

CI inspection and remediation are explicitly outside this task. Full Fast/Integration lanes and a repository-wide browser campaign are not run, following the requested focused-test scope.

UX scenario follow-up: **Not applicable** to a new reusable journey: existing create-Agent and create-Flow/Entry journeys cover these headers and navigation. Their regression assertions are maintained; targeted component contracts cover explicit coordinates, absent context service, incomplete destinations, final placement and blocking privacy states. Temporary native browser probes exercise actual list navigation, invalid-draft guards, Escape/cancel/focus, touch target and loading failure return. The existing broader follow-ups #1234/#1236 retain their maintainer-validation/merge gate.
