export type ProductHost = 'console' | 'workplace';
export type SurfaceCoverageState = 'covered' | 'partial' | 'planned';

export interface ApplicationSurface {
  id: string;
  host: ProductHost;
  source: string;
  routes: readonly string[];
  fixtureKeys: readonly string[];
  pageObject: string;
  journey?: string;
  specification: string;
  coverage: SurfaceCoverageState;
  trackingIssue?: number;
}

const consolePages = 'src/Agentstration.Web/Components/Pages';
const razorPages = 'src/Agentstration.Web/Pages';
const workplacePages = 'src/Agentstration.Workplace.Web/Components/Pages';

function planned(
  id: string,
  source: string,
  routes: readonly string[],
  trackingIssue: number,
  fixtureKeys: readonly string[] = [],
  host: ProductHost = 'console',
): ApplicationSurface {
  return {
    id,
    host,
    source,
    routes,
    fixtureKeys,
    pageObject: `src/pages/${id}.page.ts`,
    specification: `tests/${id}.spec.ts`,
    coverage: 'planned',
    trackingIssue,
  };
}

export const applicationSurfaces: readonly ApplicationSurface[] = [
  {
    id: 'console-home', host: 'console', source: `${consolePages}/Home.razor`, routes: ['/'], fixtureKeys: [],
    pageObject: 'src/pages/product.pages.ts', journey: 'authenticate-console',
    specification: 'tests/console-authentication.spec.ts', coverage: 'covered',
  },
  {
    id: 'login', host: 'console', source: `${razorPages}/Login.cshtml`, routes: ['/login'], fixtureKeys: [],
    pageObject: 'src/pages/login.page.ts', journey: 'authenticate-console',
    specification: 'tests/console-authentication.spec.ts', coverage: 'covered',
  },
  {
    id: 'organization-workspaces', host: 'console', source: `${consolePages}/OrganizationWorkspaces.razor`,
    routes: ['/settings/organization/workspaces'], fixtureKeys: [], pageObject: 'src/pages/organization-workspaces.page.ts',
    journey: 'create-workspace', specification: 'tests/create-workspace.spec.ts', coverage: 'covered',
  },
  {
    id: 'agent-editor', host: 'console', source: `${consolePages}/AgentEditor.razor`,
    routes: ['/agents/new', '/agents/{Name}'], fixtureKeys: ['name'], pageObject: 'src/pages/agent-editor.page.ts',
    journey: 'create-agent', specification: 'tests/create-agent.spec.ts', coverage: 'covered',
  },
  {
    id: 'new-flow', host: 'console', source: `${consolePages}/NewFlow.razor`, routes: ['/flows/new'], fixtureKeys: [],
    pageObject: 'src/pages/flow-editor.page.ts', journey: 'create-handoff-flow',
    specification: 'tests/create-flow-entry.spec.ts', coverage: 'covered',
  },
  {
    id: 'flow-orchestration-editor', host: 'console', source: `${consolePages}/FlowOrchestrationEditor.razor`,
    routes: ['/flows/{FlowId}/orchestration', '/namespaces/{FlowNamespace}/flows/{FlowId}/orchestration'],
    fixtureKeys: ['flowId', 'flowNamespace'], pageObject: 'src/pages/flow-editor.page.ts',
    journey: 'create-handoff-flow', specification: 'tests/create-flow-entry.spec.ts', coverage: 'partial', trackingIssue: 288,
  },
  {
    id: 'entry-editor', host: 'console', source: `${consolePages}/EntryEditor.razor`,
    routes: ['/entries/{Name}', '/namespaces/{EntryNamespace}/entries/{Name}'], fixtureKeys: ['name', 'entryNamespace'],
    pageObject: 'src/pages/entry-editor.page.ts', journey: 'create-entry',
    specification: 'tests/create-flow-entry.spec.ts', coverage: 'partial', trackingIssue: 291,
  },

  planned('access-denied', `${razorPages}/AccessDenied.cshtml`, ['/access-denied'], 290),
  planned('account-pat', `${razorPages}/Account/Pat.cshtml`, ['/account/pat'], 289),
  planned('account-security', `${razorPages}/Account/Security.cshtml`, ['/account/security'], 289),
  planned('bootstrap-account', `${razorPages}/Bootstrap.cshtml`, ['/bootstrap'], 289),
  planned('logout', `${razorPages}/Logout.cshtml`, ['/logout'], 289),

  planned('agents', `${consolePages}/Agents.razor`, ['/agents'], 291),
  planned('agent-runner', `${consolePages}/AgentRunner.razor`, ['/agents/{Name}/run', '/runs/{RunId}'], 288, ['name', 'runId']),
  planned('agent-runs', `${consolePages}/AgentRuns.razor`, ['/agent-runs'], 288),
  planned('namespaced-agent-details', `${consolePages}/NamespacedAgentDetails.razor`, ['/namespaces/{AgentNamespace}/agents/{Name}'], 291, ['agentNamespace', 'name']),
  planned('deployments', `${consolePages}/Deployments.razor`, ['/deployments'], 291),

  planned('flows', `${consolePages}/Flows.razor`, ['/flows'], 288),
  planned('flow-details', `${consolePages}/FlowDetails.razor`, ['/flows/{FlowId}', '/namespaces/{FlowNamespace}/flows/{FlowId}'], 288, ['flowId', 'flowNamespace']),
  planned('flow-designer', `${consolePages}/FlowDesigner.razor`, ['/flows/{FlowId}/designer', '/namespaces/{FlowNamespace}/flows/{FlowId}/designer'], 288, ['flowId', 'flowNamespace']),
  planned('flow-runs', `${consolePages}/FlowRuns.razor`, ['/flow-runs'], 288),
  planned('flow-run-details', `${consolePages}/FlowRunDetails.razor`, ['/flow-runs/{RunId}'], 288, ['runId']),
  planned('run-events', `${consolePages}/RunEvents.razor`, ['/run-events'], 288),
  planned('tasks', `${consolePages}/Tasks.razor`, ['/tasks', '/work'], 288),
  planned('task-details', `${consolePages}/TaskDetails.razor`, ['/tasks/{TaskId:guid}'], 288, ['taskId']),
  planned('task-flow-run-details', `${consolePages}/TaskFlowRunDetails.razor`, ['/tasks/{TaskId:guid}/flowruns/{RunId}'], 288, ['taskId', 'runId']),

  planned('entries', `${consolePages}/Entries.razor`, ['/entries'], 291),
  planned('model-profiles', `${consolePages}/ModelProfiles.razor`, ['/modelprofiles'], 291),
  planned('model-profile-editor', `${consolePages}/ModelProfileEditor.razor`, ['/modelprofiles/new', '/modelprofiles/{Name}'], 291, ['name']),
  planned('model-providers', `${consolePages}/ModelProviders.razor`, ['/modelproviders'], 291),
  planned('model-provider-details', `${consolePages}/ModelProviderDetails.razor`, ['/modelproviders/new', '/modelproviders/{Name}'], 291, ['name']),
  planned('runtime-profiles', `${consolePages}/RuntimeProfiles.razor`, ['/runtimeprofiles'], 291),
  planned('runtime-profile-editor', `${consolePages}/RuntimeProfileEditor.razor`, ['/runtimeprofiles/new', '/runtimeprofiles/{Name}'], 291, ['name']),
  planned('secrets', `${consolePages}/Secrets.razor`, ['/secrets'], 291),
  planned('secret-editor', `${consolePages}/SecretEditor.razor`, ['/secrets/new', '/secrets/{Name}'], 291, ['name']),
  planned('vaults', `${consolePages}/Vaults.razor`, ['/vaults'], 291),
  planned('vault-editor', `${consolePages}/VaultEditor.razor`, ['/vaults/new', '/vaults/{Name}'], 291, ['name']),
  planned('tools', `${consolePages}/Tools.razor`, ['/tools'], 291),
  planned('tool-details', `${consolePages}/ToolDetails.razor`, ['/tools/{Name}'], 291, ['name']),
  planned('tool-providers', `${consolePages}/ToolProviders.razor`, ['/tools/providers'], 291),
  planned('tool-provider-editor', `${consolePages}/ToolProviderEditor.razor`, ['/tools/providers/new', '/tools/providers/{Name}'], 291, ['name']),
  planned('tool-definitions', `${consolePages}/ToolDefinitions.razor`, ['/tools/definitions'], 291),
  planned('tool-definition-editor', `${consolePages}/ToolDefinitionEditor.razor`, ['/tools/definitions/new', '/tools/definitions/{Name}'], 291, ['name']),
  planned('tool-governance-audit', `${consolePages}/ToolGovernanceAudit.razor`, ['/tool-governance/{Owner}/{RunId}'], 291, ['owner', 'runId']),
  planned('triggers', `${consolePages}/Triggers.razor`, ['/triggers'], 291),
  planned('trigger-details', `${consolePages}/TriggerDetails.razor`, ['/triggers/{Name}'], 291, ['name']),
  planned('trigger-editor', `${consolePages}/TriggerEditor.razor`, ['/triggers/new', '/triggers/{Name}/edit'], 291, ['name']),

  planned('extensions', `${consolePages}/Extensions.razor`, ['/extensions'], 287),
  planned('extension-details', `${consolePages}/ExtensionDetails.razor`, ['/extensions/{RegistrationName}', '/extensions/enrollment/{EnrollmentInstanceId:guid}'], 287, ['registrationName', 'enrollmentInstanceId']),
  planned('source-providers', `${consolePages}/SourceProviders.razor`, ['/sourceproviders'], 287),
  planned('source-provider-details', `${consolePages}/SourceProviderDetails.razor`, ['/sourceproviders/new', '/sourceproviders/{Name}'], 287, ['name']),
  planned('source-registries', `${consolePages}/SourceRegistries.razor`, ['/settings/source-registries', '/settings/source-registries/new', '/settings/source-registries/{Name}'], 287, ['name']),
  planned('source-registry-discovery', `${consolePages}/SourceRegistryDiscovery.razor`, ['/settings/source-registries/discovery'], 287),
  planned('sources', `${consolePages}/Sources.razor`, ['/settings/sources', '/settings/sources/{Publisher}/{Name}'], 287, ['publisher', 'name']),
  planned('bootstrap-profiles', `${consolePages}/BootstrapProfiles.razor`, ['/settings/bootstrap'], 287),
  planned('packs', `${consolePages}/Packs.razor`, ['/packs'], 287),
  planned('pack-composer', `${consolePages}/PackComposer.razor`, ['/pack-projects/new'], 287),
  planned('pack-project-details', `${consolePages}/PackProjectDetails.razor`, ['/pack-projects/{ProjectId:guid}'], 287, ['projectId']),
  planned('resource-scopes', `${consolePages}/ResourceScopes.razor`, ['/settings/resource-scopes'], 287),

  planned('management', `${consolePages}/Management.razor`, ['/management'], 290),
  planned('cleanup', `${consolePages}/Cleanup.razor`, ['/cleanup'], 290),
  planned('settings', `${consolePages}/Settings.razor`, ['/settings'], 289),
  planned('profile-settings', `${consolePages}/ProfileSettings.razor`, ['/settings/profile'], 289),
  planned('organization', `${consolePages}/Organization.razor`, ['/settings/organization'], 289),
  planned('organization-access', `${consolePages}/OrganizationAccess.razor`, ['/settings/organization/access'], 289),
  planned('organization-members', `${consolePages}/OrganizationMembers.razor`, ['/settings/organization/members'], 289),
  planned('organization-member-details', `${consolePages}/OrganizationMemberDetails.razor`, ['/settings/organization/members/{UserId:guid}'], 289, ['userId']),
  planned('organization-security-audit', `${consolePages}/OrganizationSecurityAudit.razor`, ['/settings/organization/security-audit'], 289),
  planned('workspaces', `${consolePages}/Workspaces.razor`, ['/workspaces'], 289),

  {
    id: 'workplace-home', host: 'workplace', source: `${workplacePages}/Home.razor`,
    routes: ['/', '/w/{WorkspaceName}', '/w/{WorkspaceName}/d/{DashboardName}', '/w/{WorkspaceName}/d/{DashboardName}/conversations/{ConversationId:guid}'],
    fixtureKeys: ['workspaceName', 'dashboardName', 'conversationId'], pageObject: 'src/pages/workplace.page.ts',
    journey: 'submit-workplace-prompt', specification: 'tests/workplace.spec.ts', coverage: 'covered',
  },
  {
    id: 'workplace-entry-start', host: 'workplace', source: `${workplacePages}/EntryStart.razor`,
    routes: ['/w/{WorkspaceName}/d/{DashboardName}/start/{EntryNamespace}/{EntryName}'],
    fixtureKeys: ['workspaceName', 'dashboardName', 'entryNamespace', 'entryName'], pageObject: 'src/pages/workplace.page.ts',
    journey: 'submit-workplace-prompt', specification: 'tests/workplace.spec.ts', coverage: 'covered',
  },
  {
    id: 'workplace-notifications', host: 'workplace', source: `${workplacePages}/Notifications.razor`,
    routes: ['/w/{WorkspaceName}/notifications'], fixtureKeys: ['workspaceName'], pageObject: 'src/pages/workplace.page.ts',
    journey: 'submit-workplace-prompt', specification: 'tests/workplace.spec.ts', coverage: 'covered',
  },
  {
    id: 'workplace-tasks', host: 'workplace', source: `${workplacePages}/Tasks.razor`, routes: ['/w/{WorkspaceName}/tasks'],
    fixtureKeys: ['workspaceName'], pageObject: 'src/pages/workplace.page.ts', journey: 'submit-workplace-prompt',
    specification: 'tests/workplace.spec.ts', coverage: 'covered',
  },
  {
    id: 'workplace-task-details', host: 'workplace', source: `${workplacePages}/TaskDetails.razor`,
    routes: ['/w/{WorkspaceName}/tasks/{TaskId:guid}'], fixtureKeys: ['workspaceName', 'taskId'],
    pageObject: 'src/pages/workplace.page.ts', journey: 'submit-workplace-prompt',
    specification: 'tests/workplace.spec.ts', coverage: 'covered',
  },
] as const;

export interface CoverageSummary {
  surfaces: number;
  coveredSurfaces: number;
  routes: number;
  coveredRoutes: number;
}

export function summarizeCoverage(surfaces: readonly ApplicationSurface[] = applicationSurfaces): CoverageSummary {
  const covered = surfaces.filter(surface => surface.coverage === 'covered');
  return {
    surfaces: surfaces.length,
    coveredSurfaces: covered.length,
    routes: surfaces.reduce((total, surface) => total + surface.routes.length, 0),
    coveredRoutes: covered.reduce((total, surface) => total + surface.routes.length, 0),
  };
}
