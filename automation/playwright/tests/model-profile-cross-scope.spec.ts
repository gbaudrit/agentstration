import { exerciseModelProfileCrossScope } from '../src/journeys/exercise-model-profile-cross-scope.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });
test.setTimeout(360_000);

test('model profile usages and cross-scope navigation preserve authorization boundaries @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseModelProfileCrossScope({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('model profile cross-scope navigation remains consistent at mobile width @responsive @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseModelProfileCrossScope({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});
