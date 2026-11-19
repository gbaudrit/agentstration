import { expect, type Page } from '@playwright/test';

/** Operate the shared navigation through its desktop rail or mobile drawer. */
export async function selectResourceSection(page: Page, path: string): Promise<void> {
  const navigation = page.locator('.resource-details-navigation .resource-navigation');
  await expect(navigation).toHaveAttribute('data-navigation-ready', 'true');
  let menu = navigation.locator('.resource-navigation-rail');
  const trigger = page.locator('.resource-header .resource-navigation-trigger');
  // JS registration can finish before the navigation has a visible layout.
  await expect(menu.or(trigger).filter({ visible: true }).first()).toBeVisible();
  if (!await menu.isVisible()) {
    await trigger.click();
    menu = navigation.locator('dialog');
    await expect(menu).toBeVisible();
  }
  const link = menu.locator(`a[data-resource-section="${path}"]`);
  if (!await link.isVisible() && path.includes('/')) {
    const parent = path.split('/')[0];
    await menu.locator(`.resource-navigation-row button[aria-controls$="-${parent}"][aria-expanded=false]`).click();
  }
  await link.click();
  await expect(page).toHaveURL(url => url.hash === '#' + path);
  await expect(navigation.locator(`.resource-navigation-rail a[data-resource-section="${path}"]`)).toHaveAttribute('aria-current', 'page');
  await expect(navigation.locator('dialog')).toHaveCount(0);
}
