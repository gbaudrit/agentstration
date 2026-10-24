import { exerciseSchemaDrivenFlowInputs } from '../src/journeys/exercise-schema-driven-flow-inputs.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ExpectedTextByLocale, SupportedTestLocales } from '../src/locales/expected-text.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test('Published Flow inputs use generated fields, validation, version synchronization, fallback, and lossless JSON @smoke', async ({ page, product }) => {
  test.setTimeout(240_000);
  const pages = new ProductPages(page);
  await exerciseSchemaDrivenFlowInputs({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('Published Flow inputs remain usable on mobile @smoke @responsive', async ({ page, product }) => {
  test.setTimeout(240_000);
  const pages = new ProductPages(page);
  await exerciseSchemaDrivenFlowInputs({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  await pages.schemaDrivenFlowInputs.assertResponsive();
});

test('Published Flow input modes are localized in English and French @smoke @locale', async ({ page, product }) => {
  test.setTimeout(240_000);
  const pages = new ProductPages(page);
  await exerciseSchemaDrivenFlowInputs({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  const fixture = pages.schemaDrivenFlowInputs.lastFixture;
  if (!fixture) throw new Error('The schema-driven Flow input journey did not expose its fixture.');

  try {
    for (const locale of SupportedTestLocales) {
      await pages.consoleAdministration.restoreLanguage(product.consoleUrl, locale);
      await pages.schemaDrivenFlowInputs.assertLocalizedModes(
        product.consoleUrl, fixture.flowName, ExpectedTextByLocale[locale].schemaDrivenSources);
    }
  } finally {
    await pages.consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
  }
});
