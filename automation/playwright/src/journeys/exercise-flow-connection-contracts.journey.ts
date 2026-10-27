import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseFlowConnectionContractsInput {
  username?: string;
  password?: string;
  responsive?: boolean;
}

export const exerciseFlowConnectionContracts: Journey<ExerciseFlowConnectionContractsInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const fixtures = await context.pages.schemaContracts.createFixtures(context.consoleUrl);
  if (input.responsive) {
    await context.pages.schemaContracts.exerciseResponsiveConnectionPrompt(context.consoleUrl, fixtures);
  } else {
    await context.pages.schemaContracts.exerciseConnectionPrompts(context.consoleUrl, fixtures);
  }
};
