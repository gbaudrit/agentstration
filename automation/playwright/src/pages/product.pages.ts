import { SharedFormsPage } from './shared-forms.page.js';
import { GovernedKnowledgePage } from './governed-knowledge.page.js';
import { SourceLifecyclePage } from './source-lifecycle.page.js';
import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { AgentEditorPage } from './agent-editor.page.js';
import { FlowEditorPage } from './flow-editor.page.js';
import { FlowDesignerPage } from './flow-designer.page.js';
import { EntryEditorPage } from './entry-editor.page.js';
import { LoginPage } from './login.page.js';
import { OrganizationWorkspacesPage } from './organization-workspaces.page.js';
import { WorkplacePage } from './workplace.page.js';
import { DashboardEditorPage } from './dashboard-editor.page.js';
import { DistributionPage } from './distribution.page.js';
import { FlowObservabilityPage } from './flow-observability.page.js';
import { ConsoleAdministrationPage } from './console-administration.page.js';
import { OperationsPage } from './operations.page.js';
import { ResourceAdministrationPage } from './resource-administration.page.js';
import { ModelAdministrationPage } from './model-administration.page.js';
import { DescendantSecretsPage } from './descendant-secrets.page.js';
import { ConsoleEntryInteractionPage } from './console-entry-interaction.page.js';
import { PlatformHealthPage } from './platform-health.page.js';
import { ResourceNamingPage } from './resource-naming.page.js';
import { FoundrySecretBindingPage } from './foundry-secret-binding.page.js';
import { ArtifactAdministrationPage } from './artifact-administration.page.js';
import { PlatformOverviewPage } from './platform-overview.page.js';
import { ToolExecutionPage } from './tool-execution.page.js';
import { RuntimeWorkerObservabilityPage } from './runtime-worker-observability.page.js';
import { AgentInstanceObservabilityPage } from './agent-instance-observability.page.js';
import { SchemaDrivenSourcesPage } from './schema-driven-sources.page.js';
import { SchemaAdministrationPage } from './schema-administration.page.js';
import { ResourceAccessPage } from './resource-access.page.js';
import { ExecutionScopesPage } from './execution-scopes.page.js';
import { HierarchicalContextPage } from './hierarchical-context.page.js';
import { OperationalWorkspacesPage } from './operational-workspaces.page.js';
import { OperationalTriggersPage } from './operational-triggers.page.js';
import { SchemaContractsPage } from './schema-contracts.page.js';
import { SchemaDrivenFlowInputsPage } from './schema-driven-flow-inputs.page.js';
import { ModelProviderAccessPage } from './model-provider-access.page.js';
import { InheritedResourceReadOnlyPage } from './inherited-resource-readonly.page.js';
import { ModelProfileCrossScopePage } from './model-profile-cross-scope.page.js';
import { ScopedFlowDesignerPage } from './scoped-flow-designer.page.js';
import { ResourceScopesPage } from './resource-scopes.page.js';
import { GovernedExtensionInventoryPage } from './governed-extension-inventory.page.js';

export class ProductPages {
  public readonly sharedForms: SharedFormsPage;
  public readonly governedExtensionInventory: GovernedExtensionInventoryPage;
  public readonly governedKnowledge: GovernedKnowledgePage;
  public readonly resourceScopes: ResourceScopesPage;
  public readonly agentEditor: AgentEditorPage;
  public readonly flowEditor: FlowEditorPage;
  public readonly flowDesigner: FlowDesignerPage;
  public readonly entryEditor: EntryEditorPage;
  public readonly login: LoginPage;
  public readonly organizationWorkspaces: OrganizationWorkspacesPage;
  public readonly workplace: WorkplacePage;
  public readonly dashboardEditor: DashboardEditorPage;
  public readonly distribution: DistributionPage;
  public readonly flowObservability: FlowObservabilityPage;
  public readonly consoleAdministration: ConsoleAdministrationPage;
  public readonly operations: OperationsPage;
  public readonly resourceAdministration: ResourceAdministrationPage;
  public readonly modelAdministration: ModelAdministrationPage;
  public readonly descendantSecrets: DescendantSecretsPage;
  public readonly consoleEntryInteraction: ConsoleEntryInteractionPage;
  public readonly platformHealth: PlatformHealthPage;
  public readonly resourceNaming: ResourceNamingPage;
  public readonly foundrySecretBinding: FoundrySecretBindingPage;
  public readonly artifacts: ArtifactAdministrationPage;
  public readonly platformOverview: PlatformOverviewPage;
  public readonly toolExecution: ToolExecutionPage;
  public readonly runtimeWorkerObservability: RuntimeWorkerObservabilityPage;
  public readonly agentInstanceObservability: AgentInstanceObservabilityPage;
  public readonly sourceLifecycle: SourceLifecyclePage;
  public readonly schemaDrivenSources: SchemaDrivenSourcesPage;
  public readonly schemaAdministration: SchemaAdministrationPage;
  public readonly resourceAccess: ResourceAccessPage;
  public readonly executionScopes: ExecutionScopesPage;
  public readonly hierarchicalContext: HierarchicalContextPage;
  public readonly operationalWorkspaces: OperationalWorkspacesPage;
  public readonly operationalTriggers: OperationalTriggersPage;
  public readonly schemaContracts: SchemaContractsPage;
  public readonly schemaDrivenFlowInputs: SchemaDrivenFlowInputsPage;
  public readonly modelProviderAccess: ModelProviderAccessPage;
  public readonly inheritedResourceReadOnly: InheritedResourceReadOnlyPage;
  public readonly modelProfileCrossScope: ModelProfileCrossScopePage;
  public readonly scopedFlowDesigner: ScopedFlowDesignerPage;

  public constructor(public readonly page: Page) {
    this.sharedForms = new SharedFormsPage(page);
    this.governedExtensionInventory = new GovernedExtensionInventoryPage(page);
    this.governedKnowledge = new GovernedKnowledgePage(page);
    this.resourceScopes = new ResourceScopesPage(page);
    this.agentEditor = new AgentEditorPage(page);
    this.flowEditor = new FlowEditorPage(page);
    this.flowDesigner = new FlowDesignerPage(page);
    this.entryEditor = new EntryEditorPage(page);
    this.login = new LoginPage(page);
    this.organizationWorkspaces = new OrganizationWorkspacesPage(page);
    this.workplace = new WorkplacePage(page);
    this.dashboardEditor = new DashboardEditorPage(page);
    this.distribution = new DistributionPage(page);
    this.flowObservability = new FlowObservabilityPage(page);
    this.consoleAdministration = new ConsoleAdministrationPage(page);
    this.operations = new OperationsPage(page);
    this.resourceAdministration = new ResourceAdministrationPage(page);
    this.modelAdministration = new ModelAdministrationPage(page);
    this.descendantSecrets = new DescendantSecretsPage(page);
    this.consoleEntryInteraction = new ConsoleEntryInteractionPage(page);
    this.platformHealth = new PlatformHealthPage(page);
    this.resourceNaming = new ResourceNamingPage(page);
    this.foundrySecretBinding = new FoundrySecretBindingPage(page);
    this.artifacts = new ArtifactAdministrationPage(page);
    this.platformOverview = new PlatformOverviewPage(page);
    this.toolExecution = new ToolExecutionPage(page);
    this.runtimeWorkerObservability = new RuntimeWorkerObservabilityPage(page);
    this.agentInstanceObservability = new AgentInstanceObservabilityPage(page);
    this.sourceLifecycle = new SourceLifecyclePage(page);
    this.schemaDrivenSources = new SchemaDrivenSourcesPage(page);
    this.schemaAdministration = new SchemaAdministrationPage(page);
    this.resourceAccess = new ResourceAccessPage(page);
    this.executionScopes = new ExecutionScopesPage(page);
    this.hierarchicalContext = new HierarchicalContextPage(page);
    this.operationalWorkspaces = new OperationalWorkspacesPage(page);
    this.operationalTriggers = new OperationalTriggersPage(page);
    this.schemaContracts = new SchemaContractsPage(page);
    this.schemaDrivenFlowInputs = new SchemaDrivenFlowInputsPage(page);
    this.modelProviderAccess = new ModelProviderAccessPage(page);
    this.inheritedResourceReadOnly = new InheritedResourceReadOnlyPage(page);
    this.modelProfileCrossScope = new ModelProfileCrossScopePage(page);
    this.scopedFlowDesigner = new ScopedFlowDesignerPage(page);
  }

  public async ensureTheme(theme: 'light' | 'dark'): Promise<void> {
    const shell = this.page.getByTestId(TestIds.console.shell);
    const toggle = this.page.getByTestId(TestIds.console.themeToggle);
    // First render can announce theme readiness before redirecting to the user's
    // saved language. Wait for that document before using its theme toggle.
    const response = await this.page.request.get(new URL('/api/identity/preferences', this.page.url()).href);
    expect(response.ok(), 'Console preferences are available to the authenticated user.').toBe(true);
    const preferences = await response.json() as { language: string | null };
    if (preferences.language) await expect(this.page.locator('html')).toHaveAttribute('lang', preferences.language);
    await this.page.locator(`[data-testid="${TestIds.console.shell}"][data-preferences-ready="true"]`).waitFor({ state: 'visible' });
    await expect(toggle).toBeEnabled();
    // Attribute reads re-resolve through login/culture reloads; evaluate loses its context.
    if (await shell.getAttribute('data-theme') === theme) return;
    await toggle.click();
    await this.page.locator(`[data-testid="${TestIds.console.shell}"].theme-${theme}`).waitFor({ state: 'visible' });
    // The class changes optimistically; subsequent fixture preference writes
    // must wait until this save has completed and the toggle is enabled again.
    await expect(toggle).toBeEnabled();
    await expect(shell).toHaveAttribute('data-theme', theme);
  }

  public get readyPlatformOverview(): Locator {
    return this.page.locator(`[data-testid="${TestIds.console.platformOverview}"][aria-busy="false"]`);
  }
}
