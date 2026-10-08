import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import type { ExpectedText } from '../locales/expected-text.js';

export class PlatformOverviewPage {
  public constructor(private readonly page: Page) {}

  public get root(): Locator {
    return this.page.getByTestId(TestIds.console.platformOverview);
  }

  public async expectContent(expected: ExpectedText['platformOverview']): Promise<void> {
    await this.root.waitFor({ state: 'visible' });
    await expect(this.root).toHaveAttribute('aria-busy', 'false');
    await expect(this.root.locator('h2.eyebrow')).toHaveText(expected.groups);
    for (const label of expected.labels) await expect(this.root.getByText(label, { exact: true }).first()).toBeVisible();
    const events = this.page.getByTestId('latest-run-events');
    await expect(events).toHaveAttribute('aria-busy', 'false');
    await expect(events.locator('a[href="/run-events"]')).toHaveCount(1);
  }

  public async expectNavigationTargets(): Promise<void> {
    const targets = ['/agents', '/flows', '/extensions', '/modelproviders', '/triggers', '/agent-runs', '/flow-runs', '/tasks', '/deployments'];
    const links = this.root.locator('a.metric-card');
    await expect(links).toHaveCount(targets.length + 1);
    for (const target of targets) await expect(this.root.locator(`a.metric-card[href="${target}"]`)).toHaveCount(1);
  }

  public async expectResponsiveLayout(): Promise<void> {
    const box = await this.root.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.width).toBeGreaterThan(0);
    expect(box!.x).toBeGreaterThanOrEqual(0);
    const cards = this.root.locator('.metric-card');
    await expect(cards.first()).toBeVisible();
    const cardBox = await cards.first().boundingBox();
    expect(cardBox).not.toBeNull();
    expect(cardBox!.width).toBeGreaterThan(0);
    expect(cardBox!.x).toBeGreaterThanOrEqual(0);
  }

  public async openNotifications(): Promise<void> {
    await this.page.getByTestId(TestIds.console.notificationsTrigger).click();
    await this.page.getByTestId(TestIds.console.notificationsPanel).waitFor({ state: 'visible' });
  }
}
