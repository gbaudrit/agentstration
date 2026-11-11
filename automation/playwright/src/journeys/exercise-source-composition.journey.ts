import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import type { SourcePoliciesInput } from './exercise-source-policies.journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export const exerciseSourceComposition: Journey<SourcePoliciesInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, input.locale ?? 'en-US');
  await context.pages.sourceComposition.exercise(context.consoleUrl, input.locale ?? 'en-US', async () => {
    await context.checkpoint({ name: Checkpoints.sourcePolicies.transformationCreated, page: context.pages.page, target: context.pages.sourceComposition.creation });
  });
  await context.checkpoint({ name: Checkpoints.sourcePolicies.composition, page: context.pages.page, target: context.pages.sourceComposition.evidence });
};
