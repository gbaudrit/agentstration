import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseExecutionScopesInput {
  username?: string;
  password?: string;
}

export const exerciseExecutionScopes: Journey<ExerciseExecutionScopesInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const scopes = context.pages.executionScopes;
  const fixture = await scopes.ensureFixture(context.consoleUrl);
  const evidence = await scopes.executeFromWorkspaceConsole(context.consoleUrl, fixture);

  await scopes.openRuntimeRun(context.consoleUrl, evidence);
  await context.checkpoint({
    name: Checkpoints.executionScopes.workspaceAgentRun,
    page: context.pages.page,
    target: scopes.agentRunDefinitionScope,
  });

  await scopes.openFlowRun(context.consoleUrl, evidence);
  await context.checkpoint({
    name: Checkpoints.executionScopes.workspaceFlowRun,
    page: context.pages.page,
    target: scopes.flowRunExecutionScope,
  });

  await scopes.recoverFlowRunThroughWorkspaceList(context.consoleUrl, evidence);
  await context.checkpoint({
    name: Checkpoints.executionScopes.workspaceRecovery,
    page: context.pages.page,
    target: scopes.flowRunExecutionScope,
  });

  await scopes.expectWorkspaceIsolation(context.consoleUrl, fixture, evidence);
  await scopes.openFlowRun(context.consoleUrl, evidence);
  await context.checkpoint({
    name: Checkpoints.executionScopes.isolation,
    page: context.pages.page,
    target: scopes.flowRunExecutionScope,
  });
  await scopes.assertLocalizedLabels(context.consoleUrl, evidence);
};
