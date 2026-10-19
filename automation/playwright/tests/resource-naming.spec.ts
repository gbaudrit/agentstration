import { exerciseResourceNaming } from '../src/journeys/exercise-resource-naming.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';
import type { Page } from '@playwright/test';

async function runResourceNamingJourney(page: Page, consoleUrl: string): Promise<void> {
  const pages = new ProductPages(page);
  await exerciseResourceNaming({ consoleUrl, workplaceUrl: consoleUrl, pages, checkpoint: ignoreCheckpoints }, {});
  await expect(pages.resourceNaming.parameterForm).toBeVisible();
}

test('resource technical names follow display names and remain immutable on desktop @smoke', async ({ page, product }) => {
  await runResourceNamingJourney(page, product.consoleUrl);
});

test('resource technical names remain usable on mobile @smoke @responsive', async ({ page, product }) => {
  await runResourceNamingJourney(page, product.consoleUrl);
});
