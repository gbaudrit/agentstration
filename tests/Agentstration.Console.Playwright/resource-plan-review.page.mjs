import { expect } from '@playwright/test';

/** The shared resource navigation replaces the former Resource Plan tabs. */
export class ResourcePlanReviewPage {
  constructor(page) { this.page = page; }

  get navigation() { return this.page.locator('.resource-details-navigation .resource-navigation'); }
  get rail() { return this.navigation.locator('.resource-navigation-rail'); }

  async expectSections() {
    await expect(this.navigation).toHaveAttribute('data-navigation-ready', 'true', { timeout: 30_000 });
    await expect(this.rail.locator('a[data-resource-section]')).toHaveCount(6);
    await expect(this.rail.locator('a[data-resource-section]')).toHaveText([
      'Overview', 'Proposed changes', 'Graph', 'Verification', 'Application', 'Activity',
    ]);
    await expect(this.rail.getByRole('link', { name: 'Application', exact: true })).toBeVisible();
  }

  async selectSection(path) {
    await expect(this.navigation).toHaveAttribute('data-navigation-ready', 'true', { timeout: 30_000 });
    const trigger = this.page.locator('.resource-header .resource-navigation-trigger');
    await expect(this.rail.or(trigger).filter({ visible: true }).first()).toBeVisible();
    let menu = this.rail;
    if (!await menu.isVisible()) {
      await trigger.click();
      menu = this.navigation.locator('dialog');
      await expect(menu).toBeVisible();
    }
    await menu.locator(`a[data-resource-section="${path}"]`).click();
    await expect(this.page).toHaveURL(url => url.hash === '#' + path);
    await expect(this.rail.locator(`a[data-resource-section="${path}"]`)).toHaveAttribute('aria-current', 'page');
    await expect(this.navigation.locator('dialog')).toHaveCount(0);
  }

  async returnToList() {
    const link = this.page.locator('.resource-header .resource-collection-link');
    await expect(link).toHaveAccessibleName('Resource Plans');
    await link.click();
    const guard = this.page.getByRole('alertdialog');
    await expect(guard).toBeVisible();
    await guard.getByRole('button', { name: 'Discard', exact: true }).click();
    await expect(this.page).toHaveURL(url => url.pathname === '/resource-plans');
  }

  async expectStaleRevision() {
    const current = this.page.locator('[data-testid^="resource-plan-view-"]').filter({ visible: true });
    await expect(current.getByText('ChangeSet belongs to an earlier revision')).toBeVisible({ timeout: 30_000 });
  }

  async reloadSavedPlan() {
    await this.page.reload({ waitUntil: 'domcontentloaded' });
    await expect(this.page.locator('[data-testid="console-shell"]')).toHaveAttribute('data-preferences-ready', 'true');
    await expect(this.navigation).toHaveAttribute('data-navigation-ready', 'true', { timeout: 30_000 });
    await expect(this.page.locator('.resource-details-body')).toHaveAttribute('aria-busy', 'false');
  }
}
