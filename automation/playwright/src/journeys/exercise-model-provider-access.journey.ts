import { Checkpoints } from '../contracts/checkpoints.js';
import type { ModelAdministrationDefinition } from '../pages/model-administration.page.js';
import type { Journey } from './journey.js';

export type ExerciseModelProviderAccessInput = Pick<ModelAdministrationDefinition,
  'providerName' | 'providerDisplayName' | 'profileName' | 'profileDisplayName'> & {
  authorizedWorkspaceName: string;
  siblingWorkspaceName: string;
  agentName: string;
  agentDisplayName: string;
  username?: string;
  password?: string;
};

export const exerciseModelProviderAccess: Journey<ExerciseModelProviderAccessInput> = async (context, input) => {
  validate(input);
  const { modelAdministration: models, resourceAccess: access, organizationWorkspaces: workspaces } = context.pages;
  await context.pages.login.signIn(context.consoleUrl, input.username, input.password);
  await context.pages.ensureTheme(context.theme ?? 'dark');

  const authorizedWorkspaceId = await workspaces.selectByName(input.authorizedWorkspaceName);
  const authorizedScope = `/workspaces/${authorizedWorkspaceId}`;
  const tenantScope = await workspaces.selectTenantForWorkspaceId(authorizedWorkspaceId);
  const siblingWorkspaceId = await workspaces.selectByName(input.siblingWorkspaceName);
  const siblingScope = `/workspaces/${siblingWorkspaceId}`;
  if (authorizedScope === siblingScope) throw new Error('The authorized and sibling Workspaces must be distinct.');

  await workspaces.selectInstance();
  await models.createProvider(context.consoleUrl, completeDefinition(input));
  await models.refreshProviderModels(context.consoleUrl, input.providerName);
  await models.openProviderAccess(context.consoleUrl, input.providerName);
  await access.addGrant(tenantScope);
  await context.checkpoint({
    name: Checkpoints.modelProviderAccess.providerGranted,
    page: context.pages.page,
    target: access.grant(tenantScope),
  });

  await workspaces.selectTenantForWorkspaceId(authorizedWorkspaceId);
  await models.createProfile(context.consoleUrl, completeDefinition(input), tenantScope);
  await models.openProfileAccess(context.consoleUrl, input.profileName, tenantScope);
  await access.addGrant(authorizedScope);
  await context.checkpoint({
    name: Checkpoints.modelProviderAccess.profileGranted,
    page: context.pages.page,
    target: access.grant(authorizedScope),
  });

  const profileSelection = `${tenantScope}|default|${input.profileName}`;
  await workspaces.select(authorizedWorkspaceId);
  await context.pages.modelProviderAccess.expectProviderHidden(context.consoleUrl, input.providerName);
  await context.pages.agentEditor.openNew(context.consoleUrl);
  await context.pages.agentEditor.fillIdentity({
    name: input.agentName,
    displayName: input.agentDisplayName,
    description: 'Workspace Agent using an explicitly granted Tenant model profile.',
  });
  await context.pages.agentEditor.configureBehavior({
    instructions: 'Return a concise deterministic response.',
    modelProfile: profileSelection,
    runtimeProfile: '/instance|default|maf-builtin',
  });
  await context.pages.agentEditor.createAndDeploy(input.agentName);
  await context.pages.modelProviderAccess.runAgent(
    context.consoleUrl,
    input.agentName,
    'Verify delegated Provider execution through the Tenant model profile.',
  );
  const agentResource = await context.pages.modelProviderAccess.readAgent(
    context.consoleUrl,
    input.agentName,
    authorizedScope,
  );
  await context.checkpoint({
    name: Checkpoints.modelProviderAccess.workspaceExecuted,
    page: context.pages.page,
    target: context.pages.modelProviderAccess.runDetails,
  });

  await workspaces.select(siblingWorkspaceId);
  await context.pages.agentEditor.openNew(context.consoleUrl);
  await context.pages.agentEditor.expectModelProfileUnavailable(profileSelection);
  await context.checkpoint({
    name: Checkpoints.modelProviderAccess.siblingDenied,
    page: context.pages.page,
    target: context.pages.agentEditor.behaviorSection,
  });

  await workspaces.selectInstance();
  await models.openProviderAccess(context.consoleUrl, input.providerName);
  await access.removeGrant(tenantScope);
  await workspaces.select(authorizedWorkspaceId);
  await context.pages.modelProviderAccess.expectAgentRunRejected(context.consoleUrl, agentResource);
  await context.checkpoint({
    name: Checkpoints.modelProviderAccess.revoked,
    page: context.pages.page,
    target: context.pages.agentEditor.form,
  });
};

function completeDefinition(input: ExerciseModelProviderAccessInput): ModelAdministrationDefinition {
  return {
    ...input,
    runtimeName: 'unused-model-provider-access-runtime',
    runtimeDisplayName: 'Unused model Provider access runtime',
  };
}

function validate(input: ExerciseModelProviderAccessInput): void {
  for (const key of [
    'providerName', 'providerDisplayName', 'profileName', 'profileDisplayName',
    'authorizedWorkspaceName', 'siblingWorkspaceName', 'agentName', 'agentDisplayName',
  ] as const) {
    if (!input[key]?.trim()) throw new Error(`Model Provider Access journey input '${key}' is required.`);
  }
  if (input.authorizedWorkspaceName === input.siblingWorkspaceName) {
    throw new Error('Model Provider Access journey requires distinct authorized and sibling Workspaces.');
  }
}
