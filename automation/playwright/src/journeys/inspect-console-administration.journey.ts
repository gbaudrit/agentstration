import { Checkpoints } from '../contracts/checkpoints.js';
import { TestIds } from '../contracts/test-ids.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface InspectConsoleAdministrationInput {
  tokenName?: string;
  username?: string;
  password?: string;
}

export const inspectConsoleAdministration: Journey<InspectConsoleAdministrationInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const administration = context.pages.consoleAdministration;

  await administration.open(context.consoleUrl, '/settings/organization', 'organization');
  await context.checkpoint({ name: Checkpoints.consoleAdministration.organization, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.consoleAdministration.organization) });
  await administration.open(context.consoleUrl, '/settings/organization/members', 'organizationMembers');
  await administration.openFirstMember();
  await context.checkpoint({ name: Checkpoints.consoleAdministration.member, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.consoleAdministration.organizationMemberDetails) });

  await administration.open(context.consoleUrl, '/settings/profile', 'profileSettings');
  await administration.selectTheme('light');
  await administration.selectTheme('dark');
  await administration.selectLanguage('fr-FR');
  await administration.selectLanguage('en-US');
  await context.checkpoint({ name: Checkpoints.consoleAdministration.preferences, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.consoleAdministration.profileSettings) });

  await administration.open(context.consoleUrl, '/account/pat', 'accountPat');
  await administration.createAndRevokeToken(input.tokenName?.trim() || 'playwright-console-administration');
  await context.checkpoint({ name: Checkpoints.consoleAdministration.token, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.consoleAdministration.accountPat) });

  await administration.open(context.consoleUrl, '/settings', 'settings');
  await administration.exerciseShellNavigation();
  await context.checkpoint({ name: Checkpoints.consoleAdministration.navigation, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.console.notificationsPanel) });
};
