import { exerciseFormWorkflow } from '../src/journeys/exercise-form-workflow.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

for (const kind of ['flow-inspector', 'source-scheduling', 'entry-form', 'workplace-prompt'] as const) {
  for (const locale of ['en-US', 'fr-FR'] as const) for (const theme of ['light', 'dark'] as const) for (const mobile of [false, true]) {
    const dimension = mobile ? '@responsive' : locale === 'fr-FR' ? '@locale' : '';
    test(`form workflow ${kind} ${locale} ${theme} ${dimension} @smoke`, async ({ page, product }) => {
      await exerciseFormWorkflow({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, { kind, locale, theme });
    });
  }
}
