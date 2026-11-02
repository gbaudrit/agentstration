import { Checkpoints } from '../contracts/checkpoints.js';
import { ExpectedTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney, type ConsoleJourneySetup } from './prepare-console.journey.js';

export interface SourceLifecycleInput extends ConsoleJourneySetup { locale?: SupportedTestLocale; }

export const administerDataSources: Journey<SourceLifecycleInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, input.locale ?? 'en-US');
  const fixture = await context.pages.executionScopes.ensureFixture(context.consoleUrl);
  await context.pages.sourceLifecycle.administer(context.consoleUrl, fixture, ExpectedTextByLocale[input.locale ?? 'en-US'].sourceLifecycle);
  await context.checkpoint({ name: Checkpoints.sourceLifecycle.acquisitions, page: context.pages.page, target: context.pages.sourceLifecycle.acquisitionHistory });
};

export const projectKnowledgeSources: Journey<SourceLifecycleInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, input.locale ?? 'en-US');
  const fixture = await context.pages.executionScopes.ensureFixture(context.consoleUrl);
  await context.pages.sourceLifecycle.project(context.consoleUrl, fixture, ExpectedTextByLocale[input.locale ?? 'en-US'].sourceLifecycle);
  await context.checkpoint({ name: Checkpoints.sourceLifecycle.projections, page: context.pages.page, target: context.pages.sourceLifecycle.snapshots });
};
