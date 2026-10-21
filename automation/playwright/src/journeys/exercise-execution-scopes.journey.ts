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
  const evidence = await scopes.executeAtScopes(context.consoleUrl, fixture);

  for (const [index, item] of evidence.entries()) {
    await scopes.openRuntimeRun(context.consoleUrl, item);
    await scopes.openFlowRun(context.consoleUrl, item);
    const checkpoint = [
      Checkpoints.executionScopes.instanceRun,
      Checkpoints.executionScopes.tenantRun,
      Checkpoints.executionScopes.workspaceRun,
    ][index];
    await context.checkpoint({
      name: checkpoint,
      page: context.pages.page,
      target: scopes.flowRunExecutionScope,
    });
  }

  await scopes.expectExactIsolation(context.consoleUrl, fixture, evidence);
  await scopes.openFlowRun(context.consoleUrl, evidence.at(-1)!);
  await context.checkpoint({
    name: Checkpoints.executionScopes.isolation,
    page: context.pages.page,
    target: scopes.flowRunExecutionScope,
  });
  await scopes.assertLocalizedLabels(context.consoleUrl, evidence.at(-1)!);
};
