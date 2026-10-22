import { Checkpoints } from '../contracts/checkpoints.js';
import { TestIds } from '../contracts/test-ids.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseSchemaDrivenSourcesInput {
  username?: string;
  password?: string;
  workspaceName?: string;
}

export const exerciseSchemaDrivenSources: Journey<ExerciseSchemaDrivenSourcesInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  await context.pages.schemaDrivenSources.provisionAndExercise(context.consoleUrl);
  await context.checkpoint({
    name: Checkpoints.schemaDrivenSources.completed,
    page: context.pages.page,
    target: context.pages.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceProjections),
  });
};
