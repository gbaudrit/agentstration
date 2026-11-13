import { exerciseSourceComposition } from '../src/journeys/exercise-source-composition.journey.js';
import { exerciseSourcePolicies } from '../src/journeys/exercise-source-policies.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ sourcePolicyFixture: true });
for (const locale of ['en-US', 'fr-FR'] as const) {
  test(`Source freshness fallback and history ${locale} @smoke`, async ({ page, product }) => {
    test.setTimeout(420_000);
    await exerciseSourcePolicies({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, { locale });
  });
  test(`Source freshness fallback and history ${locale} on mobile @smoke @responsive`, async ({ page, product }) => {
    test.setTimeout(420_000);
    await exerciseSourcePolicies({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, { locale });
  });
}

for (const locale of ['en-US', 'fr-FR'] as const) {
  test(`Source Flow composition ${locale} @smoke`, async ({ page, product }) => {
    test.setTimeout(420_000);
    await exerciseSourceComposition({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, { locale });
  });
  test(`Source Flow composition ${locale} on mobile @smoke @responsive`, async ({ page, product }) => {
    test.setTimeout(420_000);
    await exerciseSourceComposition({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, { locale });
  });
}
