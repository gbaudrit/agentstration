import { TestIds } from '../src/contracts/test-ids.js';
import { createWorkspace, type CreateWorkspaceInput } from '../src/journeys/create-workspace.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { ExpectedTextByLocale } from '../src/locales/expected-text.js';
import { expect, test } from '../src/fixtures/test.js';

const workspace: CreateWorkspaceInput = {
  name: 'playwright-campaign',
  displayName: 'Playwright campaign',
};

test('an administrator can create and select a campaign workspace @smoke', async ({ page, product }) => {
  await createWorkspace({
    ...product,
    pages: new ProductPages(page),
    checkpoint: ignoreCheckpoints,
  }, workspace);

  const row = page.getByTestId(TestIds.organizationWorkspaces.row)
    .filter({ has: page.locator('code').getByText(workspace.name, { exact: true }) });
  const workspaceId = await row.getAttribute('data-workspace-id');
  expect(workspaceId).not.toBeNull();
  await expect(page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-workspace-id', workspaceId!);
  await expect(page.getByTestId(TestIds.organizationWorkspaces.create)).toHaveAccessibleName(
    ExpectedTextByLocale['en-US'].organizationWorkspaces.createWorkspace,
  );
});
