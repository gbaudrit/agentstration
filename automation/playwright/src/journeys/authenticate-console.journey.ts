import type { Journey } from './journey.js';

export interface AuthenticateConsoleInput {
  username?: string;
  password?: string;
}

export const authenticateConsole: Journey<AuthenticateConsoleInput> = async (context, input) => {
  await context.pages.login.signIn(context.consoleUrl, input.username, input.password);
  await context.pages.page.getByTestId('console-shell').waitFor({ state: 'visible' });
  const overview = context.pages.page.locator('[data-testid="platform-overview"][aria-busy="false"]');
  await overview.waitFor({ state: 'visible' });
  await overview.locator('[aria-busy="true"]').waitFor({ state: 'detached' });
  await context.checkpoint({
    name: 'console-home',
    page: context.pages.page,
    target: overview,
  });
};
