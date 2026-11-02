import { Checkpoints } from '../contracts/checkpoints.js';
import { ExpectedTextByLocale } from '../locales/expected-text.js';
import type { Journey } from './journey.js';
import type { SourceLifecycleInput } from './source-lifecycle.journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export const inspectAcquisitionFailure: Journey<SourceLifecycleInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const locale = input.locale ?? 'en-US';
  await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, locale);
  const fixture = await context.pages.executionScopes.ensureFixture(context.consoleUrl);
  await context.pages.sourceLifecycle.inspectAcquisitionFailure(context.consoleUrl, fixture,
    ExpectedTextByLocale[locale].acquisitionFailure);
  await context.checkpoint({ name: Checkpoints.sourceLifecycle.acquisitionFailure,
    page: context.pages.page, target: context.pages.sourceLifecycle.failureTopology });
};
