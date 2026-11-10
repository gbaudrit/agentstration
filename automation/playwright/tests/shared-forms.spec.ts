import { SharedFormCases } from '../src/contracts/shared-form-cases.js';
import { exerciseSharedForm } from '../src/journeys/exercise-shared-form.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

for (const definition of SharedFormCases) {
  for (const locale of ['en-US', 'fr-FR'] as const) {
    for (const theme of ['light', 'dark'] as const) {
      for (const mobile of [false, true]) {
        const dimension = mobile ? '@responsive' : locale === 'fr-FR' ? '@locale' : '';
        test(`shared form ${definition.id} ${locale} ${theme} ${mobile ? 'mobile' : 'desktop'} ${dimension} @smoke`, async ({ page, product }) => {
          await exerciseSharedForm({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, { definition, locale, theme });
        });
      }
    }
  }
}
