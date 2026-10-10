import { ProductPages } from '../src/pages/product.pages.js';
import { exerciseFlowTransitionEditing } from '../src/journeys/exercise-flow-transition-editing.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { test } from '../src/fixtures/test.js';

test('Flow transition creation, reconnection, undo/redo, invalid drop, and published read-only behavior are reusable @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseFlowTransitionEditing({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});
