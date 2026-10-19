import { expect, type Locator, type Page } from '@playwright/test';
import { ExpectedTextByLocale } from '../locales/expected-text.js';

const text = ExpectedTextByLocale['en-US'].platformHealth;

export class PlatformHealthPage {
  public constructor(private readonly page: Page) {}

  public get indicator(): Locator {
    return this.page.getByRole('status').filter({ has: this.page.getByText(text.label, { exact: true }) });
  }

  public async open(consoleUrl: string, path: '/' | '/agents'): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}${path}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Console route '${path}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.indicator.waitFor({ state: 'visible' });
  }

  public async navigateToAgents(): Promise<void> {
    await this.page.locator('a[href="/agents"]').first().click();
    await this.page.waitForURL(url => url.pathname === '/agents');
  }

  public async expectConnecting(): Promise<void> {
    await this.expectStatus(text.connecting);
  }

  public async expectOperational(): Promise<void> {
    await this.expectStatus(text.operational);
    await expect(this.indicator.locator('.health-success')).toBeVisible();
  }

  public async expectPartiallyUnavailable(): Promise<void> {
    await this.expectStatus(text.partiallyUnavailable);
    await expect(this.indicator.locator('.health-danger')).toBeVisible();
  }

  private async expectStatus(status: string): Promise<void> {
    await expect(this.indicator).toHaveAttribute('aria-label', `${text.label}: ${status}`);
  }
}
