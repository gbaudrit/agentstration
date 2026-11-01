import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseScopedFlowDesignerInput {
  username?: string;
  password?: string;
}

export const exerciseScopedFlowDesigner: Journey<ExerciseScopedFlowDesignerInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const fixture = await context.pages.executionScopes.ensureFixture(context.consoleUrl);
  await context.pages.scopedFlowDesigner.exercise(context.consoleUrl, fixture);
  await context.checkpoint({
    name: Checkpoints.scopedFlowDesigner.publicationFailure,
    page: context.pages.page,
    target: context.pages.scopedFlowDesigner.publicationError,
  });
};
