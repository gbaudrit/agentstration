import { Checkpoints } from '../contracts/checkpoints.js';
import { ExpectedTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface InspectPlatformOverviewInput {
  username?: string;
  password?: string;
  workspaceName?: string;
  locale?: SupportedTestLocale;
}

export const inspectPlatformOverview: Journey<InspectPlatformOverviewInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  if (input.locale === 'fr-FR') await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, 'fr-FR');
  const expected = ExpectedTextByLocale[input.locale ?? 'en-US'];
  const overview = context.pages.platformOverview;

  await context.pages.login.signIn(context.consoleUrl);
  await overview.expectContent(expected.platformOverview);
  await overview.expectNavigationTargets();
  await overview.openNotifications();
  await context.checkpoint({ name: Checkpoints.platformOverview.content, page: context.pages.page, target: overview.root });
  await overview.expectResponsiveLayout();
  await context.checkpoint({ name: Checkpoints.platformOverview.responsive, page: context.pages.page, target: overview.root });
};
