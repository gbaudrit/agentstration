import { createWorkspace } from '../src/journeys/create-workspace.journey.js';
import { exerciseModelProviderAccess } from '../src/journeys/exercise-model-provider-access.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';

test.use({ aiProvider: 'Managed' });

test('Model Provider access delegates authoring and execution without direct descendant discovery @smoke', async ({ page, product }) => {
  await runJourney(page, product);
});

test('Model Provider access remains operable on a mobile viewport @responsive', async ({ page, product }) => {
  await runJourney(page, product);
});

async function runJourney(page: import('@playwright/test').Page, product: import('../src/fixtures/product-hosts.js').ProductHosts): Promise<void> {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await createWorkspace(context, { name: 'provider-access-authorized', displayName: 'Provider access authorized' });
  await createWorkspace(context, { name: 'provider-access-sibling', displayName: 'Provider access sibling' });

  await exerciseModelProviderAccess(context, {
    providerName: 'provider-access-ollama',
    providerDisplayName: 'Provider access Ollama',
    profileName: 'provider-access-reasoning',
    profileDisplayName: 'Provider access reasoning',
    authorizedWorkspaceName: 'provider-access-authorized',
    siblingWorkspaceName: 'provider-access-sibling',
    agentName: 'provider-access-agent',
    agentDisplayName: 'Provider access Agent',
  });

  await expect(page).toHaveURL(/[?&]context=%2Fworkspaces%2F/);
}
