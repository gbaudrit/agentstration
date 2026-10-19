import { exerciseSchemaDrivenSources } from '../src/journeys/exercise-schema-driven-sources.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ExpectedTextByLocale, SupportedTestLocales } from '../src/locales/expected-text.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test('Data Source and Knowledge Source inputs follow exact published schemas @smoke', async ({ page, product }) => {
  test.setTimeout(360_000);
  const pages = new ProductPages(page);
  await exerciseSchemaDrivenSources({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  const fixture = pages.schemaDrivenSources.lastFixture;
  if (!fixture) throw new Error('The schema-driven source journey did not expose its fixture.');

  try {
    for (const locale of SupportedTestLocales) {
      await pages.consoleAdministration.restoreLanguage(product.consoleUrl, locale);
      await pages.schemaDrivenSources.assertLocalizedEditorModes(
        product.consoleUrl, fixture.dataSourceName, ExpectedTextByLocale[locale].schemaDrivenSources);
    }
  } finally {
    await pages.consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
  }
});

test('Schema-driven source inputs remain usable on mobile @smoke @responsive', async ({ page, product }) => {
  test.setTimeout(360_000);
  const pages = new ProductPages(page);
  await exerciseSchemaDrivenSources({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  await pages.schemaDrivenSources.assertResponsive();
});
