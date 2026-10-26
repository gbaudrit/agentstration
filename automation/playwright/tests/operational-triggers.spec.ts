import { exerciseOperationalTriggers } from '../src/journeys/exercise-operational-triggers.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });
test.setTimeout(360_000);

test('Instance Trigger targets an Instance Flow through its internal operational Workspace @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseOperationalTriggers({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('Instance Trigger journey remains usable at mobile width @responsive @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await pages.login.signIn(product.consoleUrl);
  const execution = await pages.executionScopes.ensureFixture(product.consoleUrl);
  const fixture = pages.operationalTriggers.fixture(execution);
  await pages.operationalTriggers.openCreate(product.consoleUrl, fixture);
  await pages.operationalTriggers.createDisabled(product.consoleUrl, fixture);
  await pages.operationalTriggers.assertResponsiveDetails();
});
