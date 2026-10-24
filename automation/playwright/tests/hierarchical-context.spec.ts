import { exerciseHierarchicalContext } from '../src/journeys/exercise-hierarchical-context.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ExpectedTextByLocale, SupportedTestLocales } from '../src/locales/expected-text.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });
test.setTimeout(360_000);

test('hierarchical contexts default-deny ancestor resources and separate Discover from Use @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseHierarchicalContext({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('hierarchical context selector remains usable at mobile width @responsive @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await pages.login.signIn(product.consoleUrl);
  await pages.hierarchicalContext.assertResponsiveHierarchy();
});

test('hierarchical context labels remain explicit in English and French @locale @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await pages.login.signIn(product.consoleUrl);
  try {
    for (const locale of SupportedTestLocales) {
      await pages.consoleAdministration.restoreLanguage(product.consoleUrl, locale);
      await pages.hierarchicalContext.assertLocalized(ExpectedTextByLocale[locale].contextSelector);
    }
  } finally {
    await pages.consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
  }
});
