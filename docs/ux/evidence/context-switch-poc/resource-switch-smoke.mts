import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, expect } from '../../../../automation/playwright/node_modules/@playwright/test/index.mjs';
import { startProductHosts } from '../../../../automation/playwright/src/fixtures/product-hosts.ts';
import { ProductPages } from '../../../../automation/playwright/src/pages/product.pages.ts';
import { selectResourceSection } from '../../../../automation/playwright/src/pages/resource-sections.ts';
import { TestIds } from '../../../../automation/playwright/src/contracts/test-ids.ts';

// One-off evidence for the shared resource frame; reusable coverage remains deferred in #1255.
const evidence = path.dirname(fileURLToPath(import.meta.url));
const product = await startProductHosts('Local');
const browser = await chromium.launch({ executablePath: process.env.AGENTSTRATION_CHROMIUM_EXECUTABLE ?? '/usr/bin/chromium', headless: true, args: ['--no-sandbox'] });
const results: object[] = [];
try {
  const context = await browser.newContext({ locale: 'fr-FR', viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  const pages = new ProductPages(page);
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await pages.login.signIn(product.consoleUrl);
  const fixture = await pages.executionScopes.ensureFixture(product.consoleUrl);
  const instanceHeaders = { 'X-Agentstration-Context': '/instance' };
  const flowName = 'shared-resource-switch';
  const created = await page.request.post(product.consoleUrl + '/api/flows/drafts', {
    headers: instanceHeaders, data: { name: flowName, displayName: 'Shared resource switch', template: 'AgentRouting', scopeRef: '/instance' },
  });
  expect(created.status(), await created.text()).toBe(201);
  const flow = await (await page.request.get(`${product.consoleUrl}/api/flows/${flowName}?scopeRef=%2Finstance`, { headers: instanceHeaders })).json();
  const policyUrl = `${product.consoleUrl}/api/resource-access-policies/${flow.uid}`;
  async function flowGrants(grants: object[]) {
    const current = await page.request.get(policyUrl, { headers: instanceHeaders });
    const response = await page.request.put(policyUrl, {
      headers: { ...instanceHeaders, ...(current.status() === 200 ? { 'If-Match': current.headers().etag } : {}) },
      data: { resource: { uid: flow.uid, ownerScopeRef: '/instance', namespace: 'default', kind: 'Flow', generation: 0 }, grants },
    });
    expect(response.status(), await response.text()).toBe(200);
  }
  await flowGrants([{ targetScopeRef: fixture.scopes.workspace, actions: ['discover'], includeDescendants: false }]);
  await pages.hierarchicalContext.setAgentAccess(product.consoleUrl, fixture, ['discover']);
  const shell = page.getByTestId(TestIds.console.shell);
  async function switchTo(scope: string) {
    await expect(shell).toHaveAttribute('data-preferences-ready', 'true');
    await expect(page.getByTestId(TestIds.console.contextSelectorTrigger)).toBeEnabled();
    await page.getByTestId(TestIds.console.contextSelectorTrigger).click();
    await (scope === '/instance' ? page.getByTestId('context-option-instance') : page.locator(`[data-context-scope-ref="${scope}"]`)).click();
    await expect(shell).toHaveAttribute('data-context-scope', scope);
  }
  const cases = [
    { family: 'agents', name: fixture.agentName, section: 'yaml', targetSection: 'yaml' },
    { family: 'flows', name: flowName, section: 'access', targetSection: 'overview' },
    { family: 'modelprofiles', name: fixture.modelProfileName, section: 'yaml', targetSection: 'yaml' },
  ];
  for (const item of cases) {
    // Legacy links without scopeRef must pin the already loaded owner before changing context.
    await page.goto(`${product.consoleUrl}/${item.family}/${item.name}?context=%2Finstance#${item.section}`);
    await selectResourceSection(page, item.section);
    await page.evaluate(() => { (window as any).__resourceSwitchDocument = 'retained'; });
    await switchTo(fixture.scopes.workspace);
    await expect(page).toHaveURL(url => url.pathname === `/${item.family}/${item.name}` && url.hash === '#' + item.targetSection);
    expect(new URL(page.url()).searchParams.get('scopeRef')).toBe('/instance');
    await expect(page.getByTestId('inherited-resource-notice')).toBeVisible();
    expect(await page.evaluate(() => (window as any).__resourceSwitchDocument)).toBe('retained');
    if (item.family === 'flows') {
      await expect(page.getByTestId(TestIds.resourceAccess.panel)).toHaveCount(0);
      await page.screenshot({ path: path.join(evidence, 'flow-workspace-retained.png'), fullPage: true });
    }
    await switchTo('/instance');
    await expect(page).toHaveURL(url => url.pathname === `/${item.family}/${item.name}`);
    await expect(page.getByTestId('inherited-resource-notice')).toHaveCount(0);
    if (item.family === 'flows') {
      await selectResourceSection(page, 'definition/identity');
      await expect(page.getByTestId('flow-definition-display-name')).toBeEnabled();
    }
    results.push({ family: item.family, retained: true, savedOwnerPinned: true, inheritedReadOnly: true, noReload: true });
  }

  // An inaccessible Instance Flow must not be replaced by a same-named Workspace Flow.
  const homonym = await page.request.post(product.consoleUrl + '/api/flows/drafts', {
    headers: { 'X-Agentstration-Context': fixture.scopes.workspace },
    data: { name: flowName, displayName: 'Workspace homonym', template: 'AgentRouting', scopeRef: fixture.scopes.workspace },
  });
  expect(homonym.status(), await homonym.text()).toBe(201);
  await flowGrants([]);
  await page.goto(`${product.consoleUrl}/flows/${flowName}?context=%2Finstance#yaml`);
  await selectResourceSection(page, 'yaml');
  await switchTo(fixture.scopes.workspace);
  await expect(page).toHaveURL(url => url.pathname === '/flows' && url.searchParams.get('context') === fixture.scopes.workspace);
  await expect(page.getByTestId(TestIds.console.contextResourceUnavailable)).toBeVisible();
  await expect(page.locator('.resource-collection-page')).toContainText('Workspace homonym');
  await page.getByTestId(TestIds.console.contextReturnPrevious).click();
  await expect(shell).toHaveAttribute('data-context-scope', '/instance');
  await expect(page).toHaveURL(url => url.pathname === `/flows/${flowName}` && url.hash === '#yaml');
  results.push({ flowUnavailableFallback: true, homonymNotOpened: true, explicitReturn: true });

  await flowGrants([{ targetScopeRef: fixture.scopes.workspace, actions: ['discover'], includeDescendants: false }]);
  await page.setViewportSize({ width: 390, height: 844 });
  await switchTo(fixture.scopes.workspace);
  await expect(page).toHaveURL(url => url.pathname === `/flows/${flowName}` && url.hash === '#yaml');
  await expect(page.getByTestId('inherited-resource-notice')).toBeVisible();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: path.join(evidence, 'flow-mobile-retained.png'), fullPage: true });
  expect(errors).toEqual([]);
  results.push({ mobileFlowRetained: true, noHorizontalOverflow: true, noBrowserErrors: true });
  await fs.writeFile(path.join(evidence, 'resource-switch-verification.json'), JSON.stringify(results, null, 2) + '\n');
  console.log(JSON.stringify(results, null, 2));
  await context.close();
} finally {
  await browser.close();
  await product.stop();
}
