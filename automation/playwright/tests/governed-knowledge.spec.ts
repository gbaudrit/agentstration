import { administerGovernedKnowledge } from '../src/journeys/administer-governed-knowledge.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });
test.setTimeout(540_000);

test('governed Knowledge and ToolSet administration pins Agent tools and protects Snapshot artifacts @smoke', async ({ page, product }) => {
  await administerGovernedKnowledge({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, {});
});

test('governed Knowledge and ToolSet administration remains usable on mobile @smoke @responsive', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await administerGovernedKnowledge({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  await pages.sourceLifecycle.assertResponsive();
});

test('governed Knowledge and ToolSet administration uses French governance and retention labels @smoke @locale', async ({ page, product }) => {
  await administerGovernedKnowledge({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, { locale: 'fr-FR' });
});
