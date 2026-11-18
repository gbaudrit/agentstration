import { journeys } from '../src/journeys/registry.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

for (const kind of ['contextual', 'registry', 'pack', 'preferences'] as const) {
  for (const locale of ['en-US', 'fr-FR'] as const) for (const theme of ['light', 'dark'] as const) for (const mobile of [false, true]) {
    const dimension = mobile ? '@responsive' : locale === 'fr-FR' ? '@locale' : '';
    test(`form adapter ${kind} ${locale} ${theme} ${dimension} @smoke`, async ({ page, product }) => {
      await journeys['exercise-form-adapter']({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, { kind, locale, theme });
    });
  }
}
