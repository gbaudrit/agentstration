/** Field adoption is a cross-cutting contract, not a claim of complete CRUD coverage.
 * family-regression keeps the existing journey ownership and its narrower assertions.
 * server-adapter denotes the alternate Console BFF login: the shared static adapter is
 * unit-tested and browser-tested on the canonical host, without claiming a second host run.
 */
export interface FormAdoptionCoverage {
  source: string;
  kind: 'shared-field' | 'family-regression' | 'server-adapter';
  specifications: readonly string[];
}
export const formAdoptionCoverage: readonly FormAdoptionCoverage[] = [
  { source: 'src/Agentstration.Console.Components/Components/Pages/Flows.razor', kind: 'shared-field', specifications: ['tests/flow-collection.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/AgentEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/AgentRunner.razor', kind: 'family-regression', specifications: ['tests/create-agent.spec.ts', 'tests/agent-instance-observability.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/BootstrapProfiles.razor', kind: 'family-regression', specifications: ['tests/distribution.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/Cleanup.razor', kind: 'family-regression', specifications: ['tests/operations.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/DataSourceEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/DataSourceProfileEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/EntryEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/ExtensionDetails.razor', kind: 'family-regression', specifications: ['tests/distribution.spec.ts', 'tests/governed-extension-inventory.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/Extensions.razor', kind: 'family-regression', specifications: ['tests/distribution.spec.ts', 'tests/governed-extension-inventory.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/FlowDetails.razor', kind: 'family-regression', specifications: ['tests/create-flow-entry.spec.ts', 'tests/flow-observability.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/FlowOrchestrationEditor.razor', kind: 'family-regression', specifications: ['tests/form-workflows.spec.ts', 'tests/flow-transition-editing.spec.ts', 'tests/schema-driven-flow-inputs.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/FlowRunDetails.razor', kind: 'family-regression', specifications: ['tests/flow-observability.spec.ts', 'tests/schema-driven-flow-inputs.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/KnowledgeSourceDetails.razor', kind: 'family-regression', specifications: ['tests/governed-knowledge.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/KnowledgeSourceEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/ModelDetails.razor', kind: 'family-regression', specifications: ['tests/model-administration.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/ModelProfileEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/ModelProviderDetails.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts', 'tests/form-adapters.spec.ts', 'tests/foundry-secret-binding.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/NamespacedAgentDetails.razor', kind: 'family-regression', specifications: ['tests/resource-naming.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/NewFlow.razor', kind: 'family-regression', specifications: ['tests/create-flow-entry.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/OrganizationMemberDetails.razor', kind: 'family-regression', specifications: ['tests/organization-structure.spec.ts', 'tests/console-administration.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/OrganizationMembers.razor', kind: 'family-regression', specifications: ['tests/form-administration.spec.ts', 'tests/organization-structure.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/OrganizationWorkspaces.razor', kind: 'family-regression', specifications: ['tests/organization-structure.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/PackComposer.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts', 'tests/form-adapters.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/PackProjectDetails.razor', kind: 'family-regression', specifications: ['tests/distribution.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/Packs.razor', kind: 'family-regression', specifications: ['tests/distribution.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/ParameterEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/ProfileSettings.razor', kind: 'family-regression', specifications: ['tests/form-adapters.spec.ts', 'tests/console-administration.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/ResourcePlanDetails.razor', kind: 'family-regression', specifications: ['tests/distribution.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/ResourcePlans.razor', kind: 'family-regression', specifications: ['tests/distribution.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/RuntimeProfileEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/SchemaEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/SecretEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/SourceProviderDetails.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/SourceRegistries.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts', 'tests/form-adapters.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/SourceRegistryDiscovery.razor', kind: 'family-regression', specifications: ['tests/distribution.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/Sources.razor', kind: 'family-regression', specifications: ['tests/form-workflows.spec.ts', 'tests/source-lifecycle.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/StagedArtifactDetails.razor', kind: 'family-regression', specifications: ['tests/artifact-content.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/ToolCategoryEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/ToolDefinitionEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/ToolDetails.razor', kind: 'family-regression', specifications: ['tests/tool-execution.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/ToolProviderEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/TriggerDetails.razor', kind: 'family-regression', specifications: ['tests/operational-triggers.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/TriggerEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/VaultEditor.razor', kind: 'shared-field', specifications: ['tests/shared-forms.spec.ts'] },
  { source: 'src/Agentstration.Console.Components/Components/Pages/Workspaces.razor', kind: 'family-regression', specifications: ['tests/create-workspace.spec.ts'] },
  { source: 'src/Agentstration.Console.Web/Pages/Login.cshtml', kind: 'server-adapter', specifications: ['tests/native-forms.spec.ts'] },
  { source: 'src/Agentstration.Web/Pages/Account/Pat.cshtml', kind: 'shared-field', specifications: ['tests/native-forms.spec.ts'] },
  { source: 'src/Agentstration.Web/Pages/Account/Security.cshtml', kind: 'shared-field', specifications: ['tests/native-forms.spec.ts'] },
  { source: 'src/Agentstration.Web/Pages/Bootstrap.cshtml', kind: 'shared-field', specifications: ['tests/native-bootstrap-forms.spec.ts'] },
  { source: 'src/Agentstration.Web/Pages/Login.cshtml', kind: 'shared-field', specifications: ['tests/native-forms.spec.ts'] },
];
