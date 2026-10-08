import { inspectPlatformOverview } from '../src/journeys/inspect-platform-overview.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test('Platform overview exposes localized metric groups and navigation @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await inspectPlatformOverview({ ...product, pages, checkpoint: ignoreCheckpoints }, { locale: 'en-US' });
});

test('Platform overview remains usable on a narrow viewport @responsive', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await inspectPlatformOverview({ ...product, pages, checkpoint: ignoreCheckpoints }, { locale: 'en-US' });
});

test('Platform overview renders the French resource catalog @locale', async ({ page, product }) => {
  const pages = new ProductPages(page);
  try {
    await inspectPlatformOverview({ ...product, pages, checkpoint: ignoreCheckpoints }, { locale: 'fr-FR' });
  } finally {
    await pages.consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
  }
});
