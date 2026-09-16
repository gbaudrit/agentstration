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

function coveredDistribution(
  id: string,
  source: string,
  routes: readonly string[],
  fixtureKeys: readonly string[] = [],
  journey?: string,
): ApplicationSurface {
  return {
    id, host: 'console', source, routes, fixtureKeys,
    pageObject: 'src/pages/distribution.page.ts',
    ...(journey ? { journey } : {}),
    specification: 'tests/distribution.spec.ts',
    coverage: 'covered',
  };
}

function coveredFlow(
  id: string,
  source: string,
  routes: readonly string[],
  fixtureKeys: readonly string[] = [],
  journey?: string,
): ApplicationSurface {
  return {
    id, host: 'console', source, routes, fixtureKeys,
    pageObject: 'src/pages/flow-observability.page.ts',
    ...(journey ? { journey } : {}),
    specification: 'tests/flow-observability.spec.ts',
    coverage: 'covered',
  };
}

function coveredConsoleAdministration(
  id: string,
  source: string,
  routes: readonly string[],
  fixtureKeys: readonly string[] = [],
  journey = 'inspect-console-administration',
): ApplicationSurface {
  return {
    id, host: 'console', source, routes, fixtureKeys,
    pageObject: 'src/pages/console-administration.page.ts', journey,
    specification: 'tests/console-administration.spec.ts', coverage: 'covered',
  };
}

function coveredOperations(id: string, source: string, routes: readonly string[]): ApplicationSurface {
  return {
    id, host: 'console', source, routes, fixtureKeys: [],
    pageObject: 'src/pages/operations.page.ts',
    specification: 'tests/operations.spec.ts', coverage: 'covered',
  };
}

function partialResourceAdministration(
  id: string,
  source: string,
  routes: readonly string[],
  fixtureKeys: readonly string[] = [],
): ApplicationSurface {
  return {
    id, host: 'console', source, routes, fixtureKeys,
    pageObject: 'src/pages/resource-administration.page.ts',
    journey: 'inspect-resource-administration',
    specification: 'tests/resource-administration.spec.ts',
    coverage: 'partial', trackingIssue: 291,
  };
}

function coveredModelAdministration(id: string, source: string, routes: readonly string[], fixtureKeys: readonly string[] = []): ApplicationSurface {
  return {
    id, host: 'console', source, routes, fixtureKeys,
    pageObject: 'src/pages/model-administration.page.ts',
    journey: 'exercise-model-administration',
    specification: 'tests/model-administration.spec.ts', coverage: 'covered',
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
    journey: 'create-handoff-flow', specification: 'tests/create-flow-entry.spec.ts', coverage: 'covered',
  },
  {
    id: 'entry-editor', host: 'console', source: `${consolePages}/EntryEditor.razor`,
    routes: ['/entries/{Name}', '/namespaces/{EntryNamespace}/entries/{Name}'], fixtureKeys: ['name', 'entryNamespace'],
    pageObject: 'src/pages/entry-editor.page.ts', journey: 'create-entry',
    specification: 'tests/create-flow-entry.spec.ts', coverage: 'partial', trackingIssue: 291,
  },

  coveredOperations('access-denied', `${razorPages}/AccessDenied.cshtml`, ['/access-denied']),
  coveredConsoleAdministration('account-pat', `${razorPages}/Account/Pat.cshtml`, ['/account/pat']),
  coveredConsoleAdministration('account-security', `${razorPages}/Account/Security.cshtml`, ['/account/security']),
  coveredConsoleAdministration('bootstrap-account', `${razorPages}/Bootstrap.cshtml`, ['/bootstrap']),
  coveredConsoleAdministration('logout', `${razorPages}/Logout.cshtml`, ['/logout']),

  partialResourceAdministration('agents', `${consolePages}/Agents.razor`, ['/agents']),
  coveredFlow('agent-runner', `${consolePages}/AgentRunner.razor`, ['/agents/{Name}/run', '/runs/{RunId}'], ['name', 'runId'], 'inspect-flow-observability'),
  coveredFlow('agent-runs', `${consolePages}/AgentRuns.razor`, ['/agent-runs'], [], 'inspect-flow-observability'),
  planned('namespaced-agent-details', `${consolePages}/NamespacedAgentDetails.razor`, ['/namespaces/{AgentNamespace}/agents/{Name}'], 291, ['agentNamespace', 'name']),
  partialResourceAdministration('deployments', `${consolePages}/Deployments.razor`, ['/deployments']),

  coveredFlow('flows', `${consolePages}/Flows.razor`, ['/flows']),
  coveredFlow('flow-details', `${consolePages}/FlowDetails.razor`, ['/flows/{FlowId}', '/namespaces/{FlowNamespace}/flows/{FlowId}'], ['flowId', 'flowNamespace']),
  coveredFlow('flow-designer', `${consolePages}/FlowDesigner.razor`, ['/flows/{FlowId}/designer', '/namespaces/{FlowNamespace}/flows/{FlowId}/designer'], ['flowId', 'flowNamespace']),
  coveredFlow('flow-runs', `${consolePages}/FlowRuns.razor`, ['/flow-runs'], [], 'inspect-flow-observability'),
  coveredFlow('flow-run-details', `${consolePages}/FlowRunDetails.razor`, ['/flow-runs/{RunId}'], ['runId'], 'inspect-flow-observability'),
  coveredFlow('run-events', `${consolePages}/RunEvents.razor`, ['/run-events'], [], 'inspect-flow-observability'),
  coveredFlow('tasks', `${consolePages}/Tasks.razor`, ['/tasks', '/work'], [], 'inspect-flow-observability'),
  coveredFlow('task-details', `${consolePages}/TaskDetails.razor`, ['/tasks/{TaskId:guid}'], ['taskId'], 'inspect-flow-observability'),
  coveredFlow('task-flow-run-details', `${consolePages}/TaskFlowRunDetails.razor`, ['/tasks/{TaskId:guid}/flowruns/{RunId}'], ['taskId', 'runId'], 'inspect-flow-observability'),

  partialResourceAdministration('entries', `${consolePages}/Entries.razor`, ['/entries']),
  coveredModelAdministration('model-profiles', `${consolePages}/ModelProfiles.razor`, ['/modelprofiles']),
  coveredModelAdministration('model-profile-editor', `${consolePages}/ModelProfileEditor.razor`, ['/modelprofiles/new', '/modelprofiles/{Name}'], ['name']),
  coveredModelAdministration('model-providers', `${consolePages}/ModelProviders.razor`, ['/modelproviders']),
  coveredModelAdministration('model-provider-details', `${consolePages}/ModelProviderDetails.razor`, ['/modelproviders/new', '/modelproviders/{Name}'], ['name']),
  coveredModelAdministration('runtime-profiles', `${consolePages}/RuntimeProfiles.razor`, ['/runtimeprofiles']),
  coveredModelAdministration('runtime-profile-editor', `${consolePages}/RuntimeProfileEditor.razor`, ['/runtimeprofiles/new', '/runtimeprofiles/{Name}'], ['name']),
  partialResourceAdministration('secrets', `${consolePages}/Secrets.razor`, ['/secrets']),
  partialResourceAdministration('secret-editor', `${consolePages}/SecretEditor.razor`, ['/secrets/new', '/secrets/{Name}'], ['name']),
  partialResourceAdministration('vaults', `${consolePages}/Vaults.razor`, ['/vaults']),
  partialResourceAdministration('vault-editor', `${consolePages}/VaultEditor.razor`, ['/vaults/new', '/vaults/{Name}'], ['name']),
  partialResourceAdministration('tools', `${consolePages}/Tools.razor`, ['/tools']),
  planned('tool-details', `${consolePages}/ToolDetails.razor`, ['/tools/{Name}'], 291, ['name']),
  partialResourceAdministration('tool-providers', `${consolePages}/ToolProviders.razor`, ['/tools/providers']),
  partialResourceAdministration('tool-provider-editor', `${consolePages}/ToolProviderEditor.razor`, ['/tools/providers/new', '/tools/providers/{Name}'], ['name']),
  partialResourceAdministration('tool-definitions', `${consolePages}/ToolDefinitions.razor`, ['/tools/definitions']),
  partialResourceAdministration('tool-definition-editor', `${consolePages}/ToolDefinitionEditor.razor`, ['/tools/definitions/new', '/tools/definitions/{Name}'], ['name']),
  planned('tool-governance-audit', `${consolePages}/ToolGovernanceAudit.razor`, ['/tool-governance/{Owner}/{RunId}'], 291, ['owner', 'runId']),
  partialResourceAdministration('triggers', `${consolePages}/Triggers.razor`, ['/triggers']),
  planned('trigger-details', `${consolePages}/TriggerDetails.razor`, ['/triggers/{Name}'], 291, ['name']),
  partialResourceAdministration('trigger-editor', `${consolePages}/TriggerEditor.razor`, ['/triggers/new', '/triggers/{Name}/edit'], ['name']),

  coveredDistribution('extensions', `${consolePages}/Extensions.razor`, ['/extensions']),
  coveredDistribution('extension-details', `${consolePages}/ExtensionDetails.razor`, ['/extensions/{RegistrationName}', '/extensions/enrollment/{EnrollmentInstanceId:guid}'], ['registrationName', 'enrollmentInstanceId']),
  coveredDistribution('source-providers', `${consolePages}/SourceProviders.razor`, ['/sourceproviders']),
  coveredDistribution('source-provider-details', `${consolePages}/SourceProviderDetails.razor`, ['/sourceproviders/new', '/sourceproviders/{Name}'], ['name']),
  coveredDistribution('source-registries', `${consolePages}/SourceRegistries.razor`, ['/settings/source-registries', '/settings/source-registries/new', '/settings/source-registries/{Name}'], ['name']),
  coveredDistribution('source-registry-discovery', `${consolePages}/SourceRegistryDiscovery.razor`, ['/settings/source-registries/discovery']),
  coveredDistribution('sources', `${consolePages}/Sources.razor`, ['/settings/sources', '/settings/sources/{Publisher}/{Name}'], ['publisher', 'name'], 'import-source'),
  coveredDistribution('bootstrap-profiles', `${consolePages}/BootstrapProfiles.razor`, ['/settings/bootstrap']),
  coveredDistribution('packs', `${consolePages}/Packs.razor`, ['/packs']),
  coveredDistribution('pack-composer', `${consolePages}/PackComposer.razor`, ['/pack-projects/new'], [], 'create-pack-project'),
  coveredDistribution('pack-project-details', `${consolePages}/PackProjectDetails.razor`, ['/pack-projects/{ProjectId:guid}'], ['projectId'], 'create-pack-project'),
  coveredDistribution('resource-scopes', `${consolePages}/ResourceScopes.razor`, ['/settings/resource-scopes']),

  coveredOperations('management', `${consolePages}/Management.razor`, ['/management']),
  coveredOperations('cleanup', `${consolePages}/Cleanup.razor`, ['/cleanup']),
  coveredConsoleAdministration('settings', `${consolePages}/Settings.razor`, ['/settings']),
  coveredConsoleAdministration('profile-settings', `${consolePages}/ProfileSettings.razor`, ['/settings/profile']),
  coveredConsoleAdministration('organization', `${consolePages}/Organization.razor`, ['/settings/organization']),
  coveredConsoleAdministration('organization-access', `${consolePages}/OrganizationAccess.razor`, ['/settings/organization/access']),
  coveredConsoleAdministration('organization-members', `${consolePages}/OrganizationMembers.razor`, ['/settings/organization/members']),
  coveredConsoleAdministration('organization-member-details', `${consolePages}/OrganizationMemberDetails.razor`, ['/settings/organization/members/{UserId:guid}'], ['userId']),
  coveredConsoleAdministration('organization-security-audit', `${consolePages}/OrganizationSecurityAudit.razor`, ['/settings/organization/security-audit']),
  coveredConsoleAdministration('workspaces', `${consolePages}/Workspaces.razor`, ['/workspaces']),

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
