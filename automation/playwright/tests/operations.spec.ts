import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';
import { TestIds } from '../src/contracts/test-ids.js';

test('operations, cleanup safeguards, and authorization boundaries render @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await authenticateConsole({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  await pages.operations.open(product.consoleUrl, '/management', 'management');
  await pages.operations.open(product.consoleUrl, '/cleanup', 'cleanup');
  await pages.operations.validateCleanupConfirmation();
  await pages.operations.open(product.consoleUrl, '/access-denied', 'accessDenied');
  await expect(page.getByTestId(TestIds.operations.accessDenied)).toBeVisible();
});
