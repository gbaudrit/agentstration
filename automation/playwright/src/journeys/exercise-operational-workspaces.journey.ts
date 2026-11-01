import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseOperationalWorkspacesInput {
  username?: string;
  password?: string;
}

export const exerciseOperationalWorkspaces: Journey<ExerciseOperationalWorkspacesInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const execution = await context.pages.executionScopes.ensureFixture(context.consoleUrl);
  const fixture = await context.pages.operationalWorkspaces.ensureFixture(context.consoleUrl, execution);
  const operational = context.pages.operationalWorkspaces;
  const hierarchy = context.pages.hierarchicalContext;

  const topology = await operational.assertOperationalTopologyHidden(context.consoleUrl, fixture);
  await hierarchy.selectTenant(execution.tenantId);
  await hierarchy.assertHierarchy({
    agentName: execution.agentName,
    agentUid: execution.agentUid,
    agentGeneration: execution.agentGeneration,
    tenantId: execution.tenantId,
    workspaceId: execution.workspaceId,
    scopes: execution.scopes,
  });
  await context.checkpoint({
    name: Checkpoints.operationalWorkspaces.topology,
    page: context.pages.page,
    target: hierarchy.selector,
  });

  const instanceRun = await operational.executeFlow(
    context.consoleUrl,
    fixture.instanceFlowName,
    execution.scopes.instance,
    execution.scopes.instance,
  );
  if (instanceRun.operationalWorkspaceId !== topology.instanceWorkspaceId) {
    throw new Error('The Instance Flow was not placed in the Instance operations Workspace.');
  }
  await context.checkpoint({
    name: Checkpoints.operationalWorkspaces.instanceRun,
    page: context.pages.page,
    target: operational.runDetails,
  });

  const tenantRun = await operational.executeFlow(
    context.consoleUrl,
    fixture.tenantFlowName,
    execution.scopes.tenant,
    execution.scopes.tenant,
  );
  if (tenantRun.operationalWorkspaceId !== topology.tenantWorkspaceId) {
    throw new Error('The Tenant Flow was not placed in the Tenant operations Workspace.');
  }
  await context.checkpoint({
    name: Checkpoints.operationalWorkspaces.tenantRun,
    page: context.pages.page,
    target: operational.runDetails,
  });

  await operational.recoverThroughAdministrativeContext(context.consoleUrl, instanceRun);
  await operational.recoverThroughAdministrativeContext(context.consoleUrl, tenantRun);
  await context.checkpoint({
    name: Checkpoints.operationalWorkspaces.recovery,
    page: context.pages.page,
    target: operational.runDetails,
  });

  await operational.expectRunsHiddenFromContext(
    context.consoleUrl,
    execution.scopes.workspace,
    [instanceRun, tenantRun],
  );
  await operational.expectRunsHiddenFromContext(
    context.consoleUrl,
    `/tenants/${execution.siblingTenantId}`,
    [instanceRun, tenantRun],
  );
  await context.checkpoint({
    name: Checkpoints.operationalWorkspaces.isolation,
    page: context.pages.page,
    target: operational.runs,
  });
};
