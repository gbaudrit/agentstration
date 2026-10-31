import { exerciseInheritedResourceReadOnly } from '../src/journeys/exercise-inherited-resource-readonly.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });
test.setTimeout(360_000);

test('inherited resources remain usable but read-only outside their owner scope @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseInheritedResourceReadOnly({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('inherited resource ownership remains clear at mobile width @responsive @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseInheritedResourceReadOnly({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});
