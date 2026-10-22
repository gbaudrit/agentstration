import { exerciseExecutionScopes } from '../src/journeys/exercise-execution-scopes.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });
test.setTimeout(360_000);

test('ancestor definitions execute in the selected Workspace and remain discoverable after navigation @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseExecutionScopes({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('definition ownership and invocation scope remain distinct at mobile width @responsive @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await pages.login.signIn(product.consoleUrl);
  const fixture = await pages.executionScopes.ensureFixture(product.consoleUrl);
  const evidence = await pages.executionScopes.executeFromWorkspaceConsole(product.consoleUrl, fixture);
  await pages.executionScopes.openRuntimeRun(product.consoleUrl, evidence);
  await pages.executionScopes.openFlowRun(product.consoleUrl, evidence);
});

test('execution scope labels are localized in English and French @locale @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await pages.login.signIn(product.consoleUrl);
  const fixture = await pages.executionScopes.ensureFixture(product.consoleUrl);
  const evidence = await pages.executionScopes.executeFromWorkspaceConsole(product.consoleUrl, fixture);
  await pages.executionScopes.assertLocalizedLabels(product.consoleUrl, evidence);
});
