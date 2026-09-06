import type { Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

export class LoginPage {
  public constructor(private readonly page: Page) {}

  public async signIn(consoleUrl: string, username = 'admin', password = 'admin'): Promise<void> {
    const response = await this.page.goto(consoleUrl, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Console returned HTTP ${response?.status() ?? 'no response'}.`);
    if (!this.page.url().includes('/login')) {
      await this.page.getByTestId(TestIds.console.shell).waitFor({ state: 'visible' });
      return;
    }

    await this.page.getByTestId(TestIds.login.username).fill(username);
    await this.page.getByTestId(TestIds.login.password).fill(password);
    await Promise.all([
      this.page.waitForURL(url => !url.pathname.startsWith('/login')),
      this.page.getByTestId(TestIds.login.submit).click(),
    ]);
  }
}
