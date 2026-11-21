import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, expect } from '../../../../automation/playwright/node_modules/@playwright/test/index.mjs';
import { startProductHosts } from '../../../../automation/playwright/src/fixtures/product-hosts.ts';
import { ProductPages } from '../../../../automation/playwright/src/pages/product.pages.ts';
import { selectResourceSection } from '../../../../automation/playwright/src/pages/resource-sections.ts';
import { TestIds } from '../../../../automation/playwright/src/contracts/test-ids.ts';

// One-off issue evidence; reusable selector journey remains deferred in #1255.
const evidence = path.dirname(fileURLToPath(import.meta.url));
const product = await startProductHosts('Local');
const browser = await chromium.launch({ executablePath: '/usr/bin/chromium', headless: true, args: ['--no-sandbox'] });
const results: object[] = [];
try {
  const context = await browser.newContext({ locale: 'fr-FR', viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  const pages = new ProductPages(page);
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await pages.login.signIn(product.consoleUrl);
  const fixture = await pages.executionScopes.ensureFixture(product.consoleUrl);
  const triggerFixture = pages.operationalTriggers.fixture(fixture);
  await pages.operationalTriggers.openCreate(product.consoleUrl, triggerFixture);
  const trigger = await pages.operationalTriggers.createDisabled(product.consoleUrl, triggerFixture);
  const headers = { 'X-Agentstration-Context': '/instance' };
  async function grant(collection: string, name: string, includeDescendants = true, enabled = true) {
    const loaded = await page.request.get(`${product.consoleUrl}/api/${collection}/${name}?scopeRef=%2Finstance`, { headers });
    expect(loaded.status(), await loaded.text()).toBe(200);
    const resource = await loaded.json();
    const url = `${product.consoleUrl}/api/resource-access-policies/${resource.uid}`;
    const current = await page.request.get(url, { headers });
    const saved = await page.request.put(url, {
      headers: { ...headers, ...(current.status() === 200 ? { 'If-Match': current.headers().etag } : {}) },
      data: { resource: { uid: resource.uid, ownerScopeRef: '/instance', namespace: resource.namespace ?? 'default', kind: collection === 'flows' ? 'Flow' : resource.kind, generation: collection === 'flows' ? 0 : resource.generation },
        grants: enabled ? [{ targetScopeRef: fixture.scopes.tenant, actions: ['discover', 'use'], includeDescendants }] : [] },
    });
    expect(saved.status(), await saved.text()).toBe(200);
    return resource;
  }
  await grant('flows', fixture.flowName);
  const shell = page.getByTestId(TestIds.console.shell);
  async function switchTo(scope: string) {
    await expect(shell).toHaveAttribute('data-preferences-ready', 'true');
    await expect(page.getByTestId(TestIds.console.contextSelectorTrigger)).toBeEnabled();
    await page.getByTestId(TestIds.console.contextSelectorTrigger).click();
    await (scope === '/instance' ? page.getByTestId('context-option-instance') : page.locator(`[data-context-scope-ref="${scope}"]`)).click();
    await expect(shell).toHaveAttribute('data-context-scope', scope);
  }
  await page.goto(`${product.consoleUrl}/triggers?context=%2Finstance`);
  const source = page.locator(`.resource-name-link[href*='/triggers/${triggerFixture.triggerName}']`);
  await expect(source).toBeVisible();
  expect(new URL((await source.getAttribute('href'))!, product.consoleUrl).searchParams.get('scopeRef')).toBe(trigger.scopeRef);
  await source.click();
  await selectResourceSection(page, 'yaml');
  await page.evaluate(() => { (window as any).__triggerSwitchDocument = 'same'; });
  await switchTo(fixture.scopes.tenant);
  await expect(page).toHaveURL(url => url.pathname === '/triggers' && url.searchParams.get('context') === fixture.scopes.tenant);
  const notice = page.getByTestId(TestIds.console.contextResourceUnavailable);
  await expect(notice).toBeVisible();
  await expect(notice.getByRole('heading')).toHaveText('Ressource indisponible dans ce périmètre');
  await page.screenshot({ path: path.join(evidence, 'context-unavailable-desktop-fr.png'), fullPage: true });
  await page.getByTestId(TestIds.console.contextReturnPrevious).click();
  await expect(shell).toHaveAttribute('data-context-scope', '/instance');
  await expect(page).toHaveURL(url => url.pathname === `/triggers/${triggerFixture.triggerName}` && url.hash === '#yaml');
  // Existing logical-scope links are repaired before the shared switch checks the saved owner.
  await page.goto(`${product.consoleUrl}/triggers/${triggerFixture.triggerName}?scopeRef=%2Finstance&context=%2Finstance#yaml`);
  await expect(page).toHaveURL(url => url.searchParams.get('scopeRef') === trigger.scopeRef && url.hash === '#yaml');
  await selectResourceSection(page, 'yaml');
  await switchTo(fixture.scopes.tenant);
  await expect(notice).toBeVisible();
  await switchTo(fixture.scopes.workspace);
  await switchTo('/instance');
  results.push({ triggerSourceOwnerCorrect: true, legacyLinkRepaired: true, tenantFallback: true, repeatedSwitchesWork: true });

  for (const [collection, name] of [['modelproviders', 'execution-scope-ollama'], ['modelprofiles', fixture.modelProfileName]]) {
    const original = await grant(collection, name);
    await page.goto(`${product.consoleUrl}/${collection}/${name}?scopeRef=%2Finstance&context=%2Finstance#yaml`);
    await selectResourceSection(page, 'yaml');
    await page.evaluate(() => { (window as any).__tenantSwitchDocument = 'same'; });
    await switchTo(fixture.scopes.tenant);
    await expect(page).toHaveURL(url => url.pathname === `/${collection}/${name}` && url.hash === '#yaml');
    await expect(page.getByTestId('inherited-resource-notice')).toBeVisible();
    const tenantRead = await page.request.get(`${product.consoleUrl}/api/${collection}/${name}?scopeRef=%2Finstance`, { headers: { 'X-Agentstration-Context': fixture.scopes.tenant } });
    expect(tenantRead.status()).toBe(200);
    expect((await tenantRead.json()).uid).toBe(original.uid);
    await switchTo(fixture.scopes.workspace);
    await expect(page).toHaveURL(url => url.pathname === `/${collection}/${name}` && url.hash === '#yaml');
    await expect(page.getByTestId('inherited-resource-notice')).toBeVisible();
    expect(await page.evaluate(() => (window as any).__tenantSwitchDocument)).toBe('same');
    await switchTo('/instance');
    await grant(collection, name, false);
    await switchTo(fixture.scopes.tenant);
    await expect(page).toHaveURL(url => url.pathname === `/${collection}/${name}` && url.hash === '#yaml');
    await switchTo(fixture.scopes.workspace);
    await expect(page).toHaveURL(url => url.pathname === `/${collection}`);
    await expect(notice).toBeVisible();
    results.push({ collection, tenantReadable: true, descendantsReadableWhenGranted: true, exactTenantGrantDoesNotReachWorkspace: true, sameUid: true, noReload: true });
    await page.getByTestId(TestIds.console.contextReturnPrevious).click();
    await switchTo('/instance');
    await grant(collection, name, true, false);
    await switchTo(fixture.scopes.tenant);
    await expect(page).toHaveURL(url => url.pathname === `/${collection}`);
    await expect(notice).toBeVisible();
  }
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: path.join(evidence, 'context-unavailable-mobile-fr.png'), fullPage: true });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  expect(errors).toEqual([]);
  results.push({ mobileNoticeFits: true, noBrowserErrors: true });
  await context.close();
  await fs.writeFile(path.join(evidence, 'trigger-tenant-verification.json'), JSON.stringify(results, null, 2) + '\n');
  console.log(JSON.stringify(results, null, 2));
} finally { await browser.close(); await product.stop(); }
