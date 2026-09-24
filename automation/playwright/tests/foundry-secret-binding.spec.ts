import { exerciseFoundrySecretBinding, type ExerciseFoundrySecretBindingInput } from '../src/journeys/exercise-foundry-secret-binding.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

const definition: ExerciseFoundrySecretBindingInput = {
  workspaceName: 'playwright-campaign',
  providerName: 'playwright-foundry',
  providerDisplayName: 'Playwright Foundry',
  secretValue: 'browser-fixture-secret-value',
};

test('Foundry provider bindings keep Secret values write-only and expose missing values @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseFoundrySecretBinding({ ...product, pages, checkpoint: ignoreCheckpoints }, definition);
});
