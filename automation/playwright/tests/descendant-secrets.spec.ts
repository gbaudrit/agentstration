import { createWorkspace } from '../src/journeys/create-workspace.journey.js';
import { TestIds } from '../src/contracts/test-ids.js';
import { exerciseDescendantSecrets } from '../src/journeys/exercise-descendant-secrets.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';

test('Console persists descendant Vault and Secret grants across reloads @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await createWorkspace(context, { name: 'descendant-grants', displayName: 'Descendant grants' });
  await exerciseDescendantSecrets(context, {
    workspaceName: 'descendant-grants',
    vault: { name: 'tenant-grants-vault', displayName: 'Tenant grants Vault' },
    workspaceSecret: { name: 'workspace-grants-secret', displayName: 'Workspace grants Secret' },
    tenantSecret: { name: 'tenant-grants-secret', displayName: 'Tenant grants Secret' },
  });
  await expect(page).toHaveURL(/\/secrets\/tenant-grants-secret\?scopeRef=/);
  await expect(page.getByTestId(TestIds.secretGrants.secretName)).toHaveValue('tenant-grants-secret');
});
