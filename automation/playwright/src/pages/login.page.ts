import type { Page } from '@playwright/test';

export class LoginPage {
  public constructor(private readonly page: Page) {}

  public async signIn(consoleUrl: string, username = 'admin', password = 'admin'): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/login`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Login page returned HTTP ${response?.status() ?? 'no response'}.`);

    await this.page.getByTestId('login-username').fill(username);
    await this.page.getByTestId('login-password').fill(password);
    await Promise.all([
      this.page.waitForURL(url => !url.pathname.startsWith('/login')),
      this.page.getByTestId('login-submit').click(),
    ]);
  }
}
