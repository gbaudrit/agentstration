import { inspectResourceScopes } from '../src/journeys/inspect-resource-scopes.journey.js';
import { SupportedTestLocales } from '../src/locales/expected-text.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });

for (const locale of SupportedTestLocales) {
  for (const responsive of [false, true]) {
    test(`resource scope hierarchy, combined filters and navigation ${locale} @smoke ${responsive ? '@responsive' : ''}`, async ({ page, product }, testInfo) => {
      const pages = new ProductPages(page);
      try {
        await inspectResourceScopes({ ...product, pages, checkpoint: async checkpoint => {
          const path = testInfo.outputPath(`${checkpoint.name}.png`);
          await page.screenshot({ path, fullPage: true });
          await testInfo.attach(checkpoint.name, { path, contentType: 'image/png' });
        } }, { locale });
      } finally {
        await pages.consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
      }
    });
  }
}
