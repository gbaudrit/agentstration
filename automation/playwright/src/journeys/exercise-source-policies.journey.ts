import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import type { SupportedTestLocale } from '../locales/expected-text.js';
import { prepareConsoleJourney, type ConsoleJourneySetup } from './prepare-console.journey.js';

export interface SourcePoliciesInput extends ConsoleJourneySetup { locale?: SupportedTestLocale; }
export const exerciseSourcePolicies: Journey<SourcePoliciesInput> = async (context, input) => {
  if (!context.sourcePolicy) throw new Error('This journey requires the isolated SourcePolicyHost clock/failure fixture.');
  await prepareConsoleJourney(context, input);
  const locale = input.locale ?? 'en-US';
  await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, locale);
  await context.pages.sourcePolicies.exerciseLifecycle(context.consoleUrl, context.sourcePolicy, locale);
  await context.checkpoint({ name: Checkpoints.sourcePolicies.lifecycle, page: context.pages.page, target: context.pages.sourcePolicies.history });
};
