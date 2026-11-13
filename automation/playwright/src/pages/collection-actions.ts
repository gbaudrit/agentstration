import type { Locator } from '@playwright/test';

/** Reveal an existing command without changing what the caller is allowed to execute. */
export async function revealCollectionAction(action: Locator): Promise<void> {
  await action.waitFor({ state: 'attached' });
  if (await action.isVisible()) return;
  const menu = action.locator('xpath=ancestor::details[contains(@class, "collection-actions-menu")][1]');
  if (await menu.count() === 1) await menu.locator(':scope > summary').click();
}
