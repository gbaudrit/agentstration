import { ProductPages } from '../src/pages/product.pages.js';
import { exerciseFlowConnectionContracts } from '../src/journeys/exercise-flow-connection-contracts.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { test } from '../src/fixtures/test.js';

test('direct Input connections offer Agent and Tool contracts @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseFlowConnectionContracts({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('direct Input contract prompts remain usable on mobile @responsive', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseFlowConnectionContracts({ ...product, pages, checkpoint: ignoreCheckpoints }, { responsive: true });
});
