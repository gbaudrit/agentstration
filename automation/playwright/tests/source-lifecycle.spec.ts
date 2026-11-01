import { administerDataSources, projectKnowledgeSources } from '../src/journeys/source-lifecycle.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });

for (const [name, journey] of [
  ['Data Source administration preserves profile revision provenance', administerDataSources],
  ['Knowledge projection aggregates acquired inputs and preserves Snapshot lineage', projectKnowledgeSources],
] as const) {
  test(`${name} @smoke`, async ({ page, product }) => {
    test.setTimeout(420_000);
    await journey({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, {});
  });
  test(`${name} on mobile @smoke @responsive`, async ({ page, product }) => {
    test.setTimeout(420_000);
    const pages = new ProductPages(page);
    await journey({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
    await pages.sourceLifecycle.assertResponsive();
  });
}

for (const [name, journey] of [
  ['Data Source administration', administerDataSources],
  ['Knowledge projection', projectKnowledgeSources],
] as const) {
  test(`${name} uses the French Console labels @smoke @locale`, async ({ page, product }) => {
    test.setTimeout(420_000);
    await journey({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, { locale: 'fr-FR' });
  });
}
