import { Checkpoints } from '../contracts/checkpoints.js';
import { TestIds } from '../contracts/test-ids.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseSchemaAdministrationInput {
  username?: string;
  password?: string;
  workspaceName?: string;
}

export const exerciseSchemaAdministration: Journey<ExerciseSchemaAdministrationInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  await context.pages.schemaAdministration.exercise(context.consoleUrl);
  await context.checkpoint({
    name: Checkpoints.schemaAdministration.completed,
    page: context.pages.page,
    target: context.pages.page.getByTestId(TestIds.schemaAdministration.versionDetails),
  });
};
