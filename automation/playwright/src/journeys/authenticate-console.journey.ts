import type { Journey } from './journey.js';
import { Checkpoints } from '../contracts/checkpoints.js';
import { TestIds } from '../contracts/test-ids.js';

export interface AuthenticateConsoleInput {
  username?: string;
  password?: string;
}

export const authenticateConsole: Journey<AuthenticateConsoleInput> = async (context, input) => {
  await context.pages.login.signIn(context.consoleUrl, input.username, input.password);
  await context.pages.ensureTheme(context.theme ?? 'dark');
  await context.pages.page.getByTestId(TestIds.console.shell).waitFor({ state: 'visible' });
  const overview = context.pages.readyPlatformOverview;
  await overview.waitFor({ state: 'visible' });
  await overview.locator('[aria-busy="true"]').waitFor({ state: 'detached' });
  await context.checkpoint({
    name: Checkpoints.console.home,
    page: context.pages.page,
    target: overview,
  });
};
