import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseSchemaDrivenFlowInputsInput {
  username?: string;
  password?: string;
  workspaceName?: string;
}

export const exerciseSchemaDrivenFlowInputs: Journey<ExerciseSchemaDrivenFlowInputsInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  await context.pages.schemaDrivenFlowInputs.provisionAndExercise(context.consoleUrl);
  await context.checkpoint({
    name: Checkpoints.schemaDrivenFlowInputs.completed,
    page: context.pages.page,
    target: context.pages.schemaDrivenFlowInputs.dialog,
  });
};
