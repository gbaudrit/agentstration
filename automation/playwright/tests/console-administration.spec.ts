import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { inspectConsoleAdministration } from '../src/journeys/inspect-console-administration.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';
import { TestIds } from '../src/contracts/test-ids.js';

test('Console account, preferences, organization, and shell mutations are operable @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await inspectConsoleAdministration({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  await expect(page.getByTestId(TestIds.console.notificationsPanel)).toBeVisible();
});

test('all Console administration routes render deterministic states @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await authenticateConsole(context, {});
  const administration = pages.consoleAdministration;

  await administration.open(product.consoleUrl, '/settings', 'settings');
  await administration.open(product.consoleUrl, '/settings/profile', 'profileSettings');
  await administration.open(product.consoleUrl, '/settings/organization', 'organization');
  await administration.open(product.consoleUrl, '/settings/organization/access', 'organizationAccess');
  await administration.open(product.consoleUrl, '/settings/organization/members', 'organizationMembers');
  await administration.openFirstMember();
  await administration.open(product.consoleUrl, '/settings/organization/security-audit', 'organizationSecurityAudit');
  await administration.openWorkspaces(product.consoleUrl);
  await administration.open(product.consoleUrl, '/account/security', 'accountSecurity');
  await administration.open(product.consoleUrl, '/account/pat', 'accountPat');
  await administration.openBootstrapRedirect(product.consoleUrl);
  await authenticateConsole(context, {});
  await administration.open(product.consoleUrl, '/logout', 'logout');
  await administration.signOut();
});

test('Console navigation adapts to a mobile viewport @smoke', async ({ page, product }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  const pages = new ProductPages(page);
  await authenticateConsole({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  await pages.consoleAdministration.open(product.consoleUrl, '/settings', 'settings');
  await expect(page.getByTestId(TestIds.console.sidebar)).toBeVisible();
  await expect(page.getByTestId(TestIds.console.commandTrigger)).toBeHidden();
  await expect(page.getByTestId(TestIds.console.breadcrumb)).toBeVisible();
});
