import { resolveCampaignWorkspaceName } from '../fixtures/campaign-workspace.js';
import type { JourneyContext } from './journey.js';

export interface ConsoleJourneySetup {
  username?: string;
  password?: string;
  workspaceName?: string;
}

export async function prepareConsoleJourney(context: JourneyContext, setup: ConsoleJourneySetup): Promise<void> {
  await context.pages.login.signIn(context.consoleUrl, setup.username, setup.password);

  const workspaceName = resolveCampaignWorkspaceName(setup.workspaceName);
  if (workspaceName) await context.pages.organizationWorkspaces.selectByName(workspaceName);

  await context.pages.ensureTheme(context.theme ?? 'dark');
}
