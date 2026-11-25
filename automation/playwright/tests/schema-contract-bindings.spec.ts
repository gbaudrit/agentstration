import { ProductPages } from '../src/pages/product.pages.js';
import { exerciseSchemaContractBindings } from '../src/journeys/exercise-schema-contract-bindings.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { TestIds } from '../src/contracts/test-ids.js';
import { test } from '../src/fixtures/test.js';

test('Flow, Agent, and ToolDefinition editors expose reusable contracts consistently @smoke', async ({ page, product }) => {
  // A cold, slow shell import must finish before the creation form is edited.
  // Otherwise context URL normalization can trigger the unsaved-changes guard.
  let delayedImports = 0;
  await page.route('**/context-selector*.js', async route => {
    if (new URL(route.request().frame().url()).pathname === '/tools/definitions/new') {
      delayedImports++;
      await new Promise(resolve => setTimeout(resolve, 1_500));
    }
    await route.continue();
  });
  const pages = new ProductPages(page);
  await exerciseSchemaContractBindings({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  await test.expect(page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-preferences-ready', 'true');
  await test.expect(page.getByRole('alertdialog')).toBeHidden();
  test.expect(delayedImports).toBeGreaterThan(0);
});
