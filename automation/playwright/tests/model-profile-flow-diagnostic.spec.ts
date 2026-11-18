import { exerciseModelProfileFlowDiagnostic } from '../src/journeys/exercise-model-profile-flow-diagnostic.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });
test.setTimeout(360_000);

test('Flow Run expands an inaccessible Agent model profile diagnostic without leaking runtime details @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseModelProfileFlowDiagnostic({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('Flow Run keeps the unavailable Model Profile diagnostic expandable at mobile width @responsive @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseModelProfileFlowDiagnostic({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});
