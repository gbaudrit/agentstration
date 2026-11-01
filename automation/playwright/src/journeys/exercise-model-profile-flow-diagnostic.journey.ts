import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseModelProfileFlowDiagnosticInput {
  username?: string;
  password?: string;
}

export const exerciseModelProfileFlowDiagnostic: Journey<ExerciseModelProfileFlowDiagnosticInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const fixture = await context.pages.executionScopes.ensureFixture(context.consoleUrl);
  await context.pages.executionScopes.exerciseUnavailableModelProfileDiagnostic(context.consoleUrl, fixture);
  await context.checkpoint({
    name: Checkpoints.modelProfileFlowDiagnostic.unavailable,
    page: context.pages.page,
    target: context.pages.executionScopes.runError,
  });
};
