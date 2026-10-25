import { ExpectedTextByLocale, SupportedTestLocales } from '../src/locales/expected-text.js';
import { exerciseOperationalWorkspaces } from '../src/journeys/exercise-operational-workspaces.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });
test.setTimeout(360_000);

test('Instance and Tenant contexts use isolated operational Workspaces @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseOperationalWorkspaces({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('operational topology remains hidden in the mobile context selector @responsive @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await pages.login.signIn(product.consoleUrl);
  const execution = await pages.executionScopes.ensureFixture(product.consoleUrl);
  const fixture = await pages.operationalWorkspaces.ensureFixture(product.consoleUrl, execution);
  await pages.operationalWorkspaces.assertOperationalTopologyHidden(product.consoleUrl, fixture);
  await pages.hierarchicalContext.assertResponsiveHierarchy();
});

test('administrative context labels remain explicit in English and French @locale @smoke', async ({ page, product }) => {
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
