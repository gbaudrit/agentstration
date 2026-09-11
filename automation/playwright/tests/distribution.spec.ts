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
  await pages.distribution.open(product.consoleUrl, '/settings/sources/playwright/offline-source', 'sources');
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
