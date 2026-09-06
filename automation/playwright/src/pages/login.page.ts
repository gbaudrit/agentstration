import type { Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { resolveLoginCredentials } from '../fixtures/login-credentials.js';

export class LoginPage {
  public constructor(private readonly page: Page) {}

  public async signIn(consoleUrl: string, username?: string, password?: string): Promise<void> {
    const response = await this.page.goto(consoleUrl, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Console returned HTTP ${response?.status() ?? 'no response'}.`);
    if (!this.page.url().includes('/login')) {
      await this.page.getByTestId(TestIds.console.shell).waitFor({ state: 'visible' });
      return;
    }

    const credentials = resolveLoginCredentials(username, password);
    await this.page.getByTestId(TestIds.login.username).fill(credentials.username);
    await this.page.getByTestId(TestIds.login.password).fill(credentials.password);
    await Promise.all([
      this.page.waitForURL(url => !url.pathname.startsWith('/login')),
      this.page.getByTestId(TestIds.login.submit).click(),
    ]);
  }
}
