import { expect, type Page } from '@playwright/test';

/** Operate the shared navigation through its desktop rail or mobile drawer. */
export async function selectResourceSection(page: Page, path: string): Promise<void> {
  const navigation = page.locator('.resource-details-navigation .resource-navigation');
  let menu = navigation.locator('.resource-navigation-rail');
  const trigger = page.locator('.resource-header .resource-navigation-trigger');
  // A Blazor resource transition can temporarily detach both controls. Only
  // choose the drawer when its trigger is visible, rather than from a missing rail.
  let useDrawer = false;
  await expect.poll(async () => {
    if (await menu.isVisible()) return true;
    useDrawer = await trigger.isVisible();
    return useDrawer;
  }).toBe(true);
  await expect(navigation).toHaveAttribute('data-navigation-ready', 'true');
  const railLink = navigation.locator(`.resource-navigation-rail a[data-resource-section="${path}"]`);
  const dialog = navigation.locator('dialog');
  // Direct fragment navigation can still be reconciling its selected section.
  // Wait for that selection instead of opening a drawer it will immediately close.
  if (new URL(page.url()).hash === '#' + path && await dialog.count() === 0) {
    await expect(railLink).toHaveAttribute('aria-current', 'page');
    await expect(page).toHaveURL(url => url.hash === '#' + path);
    return;
  }
  if (useDrawer) {
    await trigger.click();
    menu = dialog;
    await expect(menu).toBeVisible();
  }
  const link = menu.locator(`a[data-resource-section="${path}"]`);
  await expect(link).toHaveCount(1);
  if (!await link.isVisible() && path.includes('/')) {
    const parent = path.split('/')[0];
    const expander = menu.locator(`.resource-navigation-row button[aria-controls$="-${parent}"]`);
    if (await expander.getAttribute('aria-expanded') === 'false') await expander.click();
  }
  await expect(link).toBeVisible();
  await link.click();
  await expect(page).toHaveURL(url => url.hash === '#' + path);
  await expect(railLink).toHaveAttribute('aria-current', 'page');
  await expect(dialog).toHaveCount(0);
}
