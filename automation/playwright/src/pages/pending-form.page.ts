import { expect, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import type { SupportedTestLocale } from '../locales/expected-text.js';
import { SharedFormsPage } from './shared-forms.page.js';

/** A pending input seeded through the real repository by the isolated fixture. */
export class PendingFormPage {
  public constructor(private readonly page: Page) {}
  public async exercise(consoleUrl: string, workplaceUrl: string, id: string, locale: SupportedTestLocale): Promise<void> {
    const read = async () => {
      const response = await this.page.request.get(`${consoleUrl}/api/workspaces/default/interactions/${id}`);
      expect(response.ok()).toBe(true);
      return await response.json();
    };
    const before = await read();
    await this.page.request.get(`${workplaceUrl}/_culture?culture=${locale}&returnUrl=%2F`);
    await this.page.goto(`${workplaceUrl}/w/default/d/home/conversations/${id}`);
    await expect(this.page.getByTestId(TestIds.workplace.conversationThread)).toBeVisible();
    const pending = this.page.locator('.pending-action');
    const field = pending.locator('textarea');
    await expect(field).toBeVisible();
    await expect(field).toHaveAccessibleName(/.+/);
    const submit = pending.locator('.action-button');
    // The restored conversation streams before its interactive circuit attaches.
    // Repeat only local draft input until the real binding enables Continue.
    await expect(async () => {
      await field.fill('');
      await field.fill('A response that must remain local.');
      await field.press('Tab');
      await expect(submit).toBeEnabled({ timeout: 1_000 });
    }).toPass({ timeout: 15_000 });
    const forms = new SharedFormsPage(this.page);
    await forms.clearField(field, locale);
    await expect(submit).toBeDisabled();
    const after = await read();
    expect(after.status).toBe(before.status);
    expect(after.pendingActionId).toBe(before.pendingActionId);
    expect(after.taskId ?? null).toBeNull();
    await forms.assertWithinViewport();
  }
}
