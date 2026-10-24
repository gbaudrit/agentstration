import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseSchemaContractBindingsInput {
  username?: string;
  password?: string;
}

export const exerciseSchemaContractBindings: Journey<ExerciseSchemaContractBindingsInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const fixtures = await context.pages.schemaContracts.createFixtures(context.consoleUrl);
  await context.pages.schemaContracts.exerciseFlowBindings(context.consoleUrl, fixtures);
  await context.pages.schemaContracts.exerciseAgentBindings(context.consoleUrl, fixtures);
  await context.pages.schemaContracts.exerciseToolFlowContracts(context.consoleUrl, fixtures);
};
