import { inspectAcquisitionFailure } from '../src/journeys/inspect-acquisition-failure.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local', acquisitionFailureFixture: true });

for (const locale of ['en-US', 'fr-FR'] as const) {
  for (const mobile of [false, true]) {
    test(`REST acquisition failure diagnostics ${locale} ${mobile ? 'mobile @responsive' : 'desktop'} @smoke${locale === 'fr-FR' && !mobile ? ' @locale' : ''}`,
      async ({ page, product }) => {
        test.setTimeout(240_000);
        const pages = new ProductPages(page);
        await inspectAcquisitionFailure({ ...product, pages, checkpoint: ignoreCheckpoints }, { locale });
      });
  }
}
