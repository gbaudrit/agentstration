import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { exerciseToolExecution } from '../src/journeys/exercise-tool-execution.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ExpectedTextByLocale, SupportedTestLocales } from '../src/locales/expected-text.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';

test('Tool simulation and real execution preserve their governed contracts @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const evidence = await exerciseToolExecution({ ...product, pages, checkpoint: ignoreCheckpoints }, {});

  expect(evidence.afterSimulation).toEqual(evidence.beforeSimulation);
  expect(evidence.simulationOutput.dryRun).toBe(true);
  expect(evidence.realOutput.dryRun).toBe(false);
  expect(evidence.afterRealExecution.notifications).toBe(evidence.beforeSimulation.notifications + 1);
});

test('Tool execution preserves the English and French locale contracts @smoke @locale', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await authenticateConsole({ ...product, pages, checkpoint: ignoreCheckpoints }, {});

  try {
    for (const locale of SupportedTestLocales) {
      await pages.consoleAdministration.restoreLanguage(product.consoleUrl, locale);
      await pages.toolExecution.open(product.consoleUrl, 'agentstration.work.notification.create');
      await pages.toolExecution.assertLocalized(ExpectedTextByLocale[locale].toolExecution);
    }
  } finally {
    await pages.consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
  }
});
