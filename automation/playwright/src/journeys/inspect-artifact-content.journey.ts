import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface InspectArtifactContentInput {
  username?: string;
  password?: string;
  workspaceName?: string;
}

export const inspectArtifactContent: Journey<InspectArtifactContentInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  await context.pages.artifacts.exerciseGovernedContent(context.consoleUrl);
};
