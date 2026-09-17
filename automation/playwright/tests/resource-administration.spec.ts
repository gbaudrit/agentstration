import { inspectResourceAdministration } from '../src/journeys/inspect-resource-administration.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';

test('Console resource lists and creation editors render deterministic states @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await inspectResourceAdministration({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  await expect(page).toHaveURL(/\/triggers\/new$/);
});
