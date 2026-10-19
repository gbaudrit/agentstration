import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ExpectedTextByLocale, SupportedTestLocales } from '../src/locales/expected-text.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test('Console navigation preserves the English and French locale contracts @smoke @locale', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await authenticateConsole({ ...product, pages, checkpoint: ignoreCheckpoints }, {});

  try {
    for (const locale of SupportedTestLocales) {
      await pages.consoleAdministration.restoreLanguage(product.consoleUrl, locale);
      await pages.consoleAdministration.assertNavigationLocale(ExpectedTextByLocale[locale].navigation);
    }
  } finally {
    await pages.consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
  }
});
