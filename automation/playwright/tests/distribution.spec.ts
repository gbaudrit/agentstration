import { randomUUID } from 'node:crypto';
import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { createPackProject } from '../src/journeys/create-pack-project.journey.js';
import { importSource } from '../src/journeys/import-source.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';

test('local AEP extensions expose inventory and detail states @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await authenticateConsole(context, {});
  await pages.distribution.openFirstExtensionDetails(product.consoleUrl);
  await pages.distribution.open(product.consoleUrl, `/extensions/enrollment/${randomUUID()}`, 'extensionDetails');
});

for (const variant of [
  { locale: 'en-US', theme: 'dark', width: 1440 },
  { locale: 'en-US', theme: 'light', width: 1440 },
  { locale: 'fr-FR', theme: 'dark', width: 390 },
  { locale: 'fr-FR', theme: 'light', width: 390 },
] as const) {
  test(`collection version identity and primary choice ${variant.locale} ${variant.theme} ${variant.width === 390 ? 'mobile @responsive' : 'desktop'} @smoke`, async ({ page, product }, testInfo) => {
    const pages = new ProductPages(page);
    await authenticateConsole({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
    await pages.distribution.openFirstExtensionDetails(product.consoleUrl);
    try {
      await page.setViewportSize({ width: variant.width, height: variant.width === 390 ? 844 : 1000 });
      await pages.consoleAdministration.restoreLanguage(product.consoleUrl, variant.locale);
      await pages.ensureTheme(variant.theme);
      const capture = async (name: string) => {
        const path = testInfo.outputPath(`${name}-${variant.locale}-${variant.theme}.png`);
        await page.screenshot({ path, fullPage: true });
        await testInfo.attach(name, { path, contentType: 'image/png' });
      };
      for (const route of ['/data-source-profiles', '/flows', '/agents', '/extensions?tab=catalog&context=%2Finstance']) {
        await pages.distribution.inspectVersionPlacement(product.consoleUrl, route, variant.locale);
        await capture(route.split('?')[0].slice(1));
        if (variant.width === 1440) {
          await pages.distribution.showCollectionTable();
          await capture(`${route.split('?')[0].slice(1)}-table`);
        }
      }
      await pages.distribution.inspectToolCollectionAlignment(product.consoleUrl, variant.width === 1440 ? 'columns' : 'stacked');
      await capture('tools');
      if (variant.width === 1440) {
        await pages.distribution.showCollectionTable();
        await capture('tools-table');
      }
      await pages.distribution.inspectDirectProfileChoice(product.consoleUrl);
      await capture('profile-primary-choice');
      const workspaceId = await pages.organizationWorkspaces.selectByName('default');
      const projectName = `compact-cards-${variant.locale.toLowerCase()}-${variant.theme}`;
      await pages.distribution.createPackProject(product.consoleUrl, {
        publisher: 'playwright', name: projectName, version: '2.3.4', displayName: projectName,
      });
      await pages.distribution.inspectCompactProjectCard(product.consoleUrl, variant.locale, projectName);
      await capture('pack-project-card');
      await pages.distribution.inspectCompactEntryCards(product.consoleUrl, variant.locale, workspaceId);
      await capture('entry-cards');
    } finally {
      await pages.consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
    }
  });
}

test('the local Git extension can back an explicitly configured Source Provider @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await authenticateConsole(context, {});
  await pages.distribution.open(product.consoleUrl, '/sourceproviders', 'sourceProviders');
  await pages.distribution.createSourceProvider(product.consoleUrl, 'playwright-git', 'Playwright Git provider');
  await expect(page).toHaveURL(url => url.pathname === '/sourceproviders/playwright-git'
    && url.searchParams.get('namespace') === 'default'
    && url.searchParams.has('scopeRef'));
});

test('Source Registry list, editor, retained official state, and discovery render offline @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await authenticateConsole(context, {});
  await pages.distribution.open(product.consoleUrl, '/settings/source-registries', 'sourceRegistries');
  await pages.distribution.open(product.consoleUrl, '/settings/source-registries/new', 'sourceRegistries');
  await pages.distribution.open(product.consoleUrl, '/settings/source-registries/agentstration-official', 'sourceRegistries');
  await pages.distribution.open(product.consoleUrl, '/settings/source-registries/discovery', 'sourceRegistryDiscovery');
});

test('a pasted Source manifest is imported without Internet and opens its details @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await importSource(context, {
    publisher: 'playwright',
    name: 'offline-source',
    displayName: 'Playwright offline source',
  });
  await pages.distribution.open(product.consoleUrl, '/settings/sources', 'sources');
  await pages.distribution.openSourceDetailsFromList('playwright', 'offline-source');
  await expect(pages.distribution.sourceDetails('playwright', 'offline-source')).toBeVisible();
});

test('Bootstrap catalog and resource ownership scopes render deterministic local state @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await authenticateConsole(context, {});
  await pages.distribution.open(product.consoleUrl, '/settings/bootstrap', 'bootstrapProfiles');
  await pages.distribution.open(product.consoleUrl, '/settings/resource-scopes', 'resourceScopes');
});

test('a Pack Project can be composed from local resources and built @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await createPackProject(context, {
    publisher: 'playwright',
    name: 'local-coverage',
    version: '1.0.0',
    displayName: 'Playwright local coverage',
  });
  await pages.distribution.open(product.consoleUrl, '/packs', 'packs');
});
