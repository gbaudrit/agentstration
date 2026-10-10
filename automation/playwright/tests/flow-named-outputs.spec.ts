import { ProductPages } from '../src/pages/product.pages.js';
import { exerciseFlowNamedOutputs } from '../src/journeys/exercise-flow-named-outputs.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { test } from '../src/fixtures/test.js';

test('Named outputs and FlowCall ports remain versioned, editable, reconnectable, and read-only after publication @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseFlowNamedOutputs({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});
