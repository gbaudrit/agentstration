import { exerciseModelAdministration, type ExerciseModelAdministrationInput } from '../src/journeys/exercise-model-administration.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';

const definition: ExerciseModelAdministrationInput = {
  providerName: 'playwright-ollama',
  providerDisplayName: 'Playwright Ollama',
  profileName: 'playwright-model',
  profileDisplayName: 'Playwright model',
  runtimeName: 'playwright-runtime',
  runtimeDisplayName: 'Playwright runtime',
};

test('model provider, model profile, and runtime profile complete their lifecycle @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseModelAdministration({ ...product, pages, checkpoint: ignoreCheckpoints }, definition);
  await expect(page).toHaveURL(/\/modelproviders$/);
});
