import { createWorkspace } from '../src/journeys/create-workspace.journey.js';
import { exerciseResourceAccess } from '../src/journeys/exercise-resource-access.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';

test('Console administers exact descendant access independently from governed definitions @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await createWorkspace(context, { name: 'resource-access-authorized', displayName: 'Resource access authorized' });
  await createWorkspace(context, { name: 'resource-access-sibling', displayName: 'Resource access sibling' });

  await exerciseResourceAccess(context, {
    authorizedWorkspaceName: 'resource-access-authorized',
    siblingWorkspaceName: 'resource-access-sibling',
    vaultName: 'hierarchical-access-vault',
    vaultDisplayName: 'Hierarchical access Vault',
    workspaceSecretName: 'authorized-vault-reference',
  });

  await expect(page).toHaveURL(/\/secrets\/new/);
});
