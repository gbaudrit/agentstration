import { ProductPages } from '../src/pages/product.pages.js';
import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { expect, test } from '../src/fixtures/test.js';

test('an administrator can open the Console @smoke', async ({ page, product }) => {
  await authenticateConsole({
    ...product,
    pages: new ProductPages(page),
    checkpoint: ignoreCheckpoints,
  }, {});

  await expect(page.getByTestId('console-shell')).toBeVisible();
  await expect(page).not.toHaveURL(/\/login/);
});
