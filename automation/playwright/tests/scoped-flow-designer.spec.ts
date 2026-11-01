import { exerciseScopedFlowDesigner } from '../src/journeys/exercise-scoped-flow-designer.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });
test.setTimeout(360_000);

test('Flow Designer catalogs, exact references, and publication feedback honor the Flow owner scope @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseScopedFlowDesigner({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});
