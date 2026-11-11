import { exerciseFormAdapter } from '../src/journeys/exercise-form-adapter.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local', organizationStructureFixture: true });
for (const locale of ['en-US', 'fr-FR'] as const) for (const theme of ['light', 'dark'] as const) for (const mobile of [false, true]) {
  const dimension = mobile ? '@responsive' : locale === 'fr-FR' ? '@locale' : '';
  test(`administration field validation ${locale} ${theme} ${dimension} @smoke`, async ({ page, product }) => {
    await exerciseFormAdapter({ ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, {
      kind: 'administration', locale, theme,
      ...(process.env.AGENTSTRATION_CONSOLE_URL ? {} : { username: 'admin', password: 'Fixture973!Admin' }),
    });
  });
}
