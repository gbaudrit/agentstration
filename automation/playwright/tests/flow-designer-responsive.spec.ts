import { ProductPages } from '../src/pages/product.pages.js';
import { exerciseFlowDesignerResponsive } from '../src/journeys/exercise-flow-designer-responsive.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { test } from '../src/fixtures/test.js';

test('Responsive Flow Designer keeps panels, focus modes, Escape, and verification accessible @smoke @responsive', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseFlowDesignerResponsive({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});
