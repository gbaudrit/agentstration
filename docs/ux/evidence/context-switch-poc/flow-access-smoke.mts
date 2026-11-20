import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, expect } from '../../../../automation/playwright/node_modules/@playwright/test/index.mjs';
import { startProductHosts } from '../../../../automation/playwright/src/fixtures/product-hosts.ts';
import { ProductPages } from '../../../../automation/playwright/src/pages/product.pages.ts';
import { selectResourceSection } from '../../../../automation/playwright/src/pages/resource-sections.ts';
import { TestIds } from '../../../../automation/playwright/src/contracts/test-ids.ts';

// One-off evidence for the reported Flow failure, outside the reusable UX catalog.
const evidence = path.dirname(fileURLToPath(import.meta.url));
const product = await startProductHosts('Local');
const browser = await chromium.launch({
  executablePath: process.env.AGENTSTRATION_CHROMIUM_EXECUTABLE ?? '/usr/bin/chromium',
  headless: true,
  args: ['--no-sandbox'],
});
const results: object[] = [];
try {
  for (const locale of ['fr-FR', 'en-US']) {
    const context = await browser.newContext({ locale, viewport: { width: 1440, height: 1000 } });
    const page = await context.newPage();
    const pages = new ProductPages(page);
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await pages.login.signIn(product.consoleUrl);
    const identity = await (await page.request.get(product.consoleUrl + '/api/identity/context')).json();
    const workspace = identity.availableWorkspaces.find((item: any) => item.name === 'default');
    const source = '/workspaces/' + workspace.id;
    const preferences = await page.request.put(product.consoleUrl + '/api/identity/preferences', {
      data: { theme: 'Dark', language: null },
    });
    expect(preferences.ok()).toBe(true);
    await page.request.get(product.consoleUrl + '/_culture?culture=auto&returnUrl=%2F');
    await page.goto(product.consoleUrl + '/flows?context=' + encodeURIComponent(source));
    await expect(page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-context-scope', source);
    await expect(page.locator('html')).toHaveAttribute('lang', locale);
    await expect(page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-preferences-ready', 'true');
    await page.evaluate(() => { (window as any).__flowAccessDocument = 'source'; });
    await page.getByTestId(TestIds.console.contextSelectorTrigger).click();
    await page.getByTestId('context-option-instance').click();
    await expect(page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-context-scope', '/instance');
    await expect(page.getByTestId(TestIds.console.breadcrumb)).toContainText(
      locale === 'fr-FR' ? 'Administration de l’Instance' : 'Instance administration');
    expect(await page.evaluate(() => (window as any).__flowAccessDocument)).toBe('source');
    const name = 'flow-access-' + locale.toLowerCase();
    await page.locator('a[href^="/flows/new"]').first().click();
    await page.locator('[data-testid="flow-create-form"][data-interactive=true]').waitFor();
    await page.getByTestId('flow-display-name').fill('Flow access regression');
    await page.getByTestId('flow-display-name').blur();
    await page.getByTestId('flow-name').fill(name);
    await page.getByTestId('flow-name').blur();
    await page.getByTestId(TestIds.creationWizard.next).click();
    await page.getByTestId(TestIds.creationWizard.next).click();
    await page.getByTestId('flow-create').click();
    await page.waitForURL(url => url.pathname.startsWith('/flows/' + name));
    await page.goto(`${product.consoleUrl}/flows/${name}?scopeRef=%2Finstance&context=%2Finstance#access`);
    await selectResourceSection(page, 'access');
    await expect(page.getByTestId(TestIds.resourceAccess.panel)).toBeVisible();
    expect((await pages.resourceAccess.identity()).version).toBe('');
    await page.getByTestId(TestIds.resourceAccess.openAdd).click();
    await page.getByTestId(TestIds.resourceAccess.target).selectOption('/tenants/' + workspace.tenantId);
    await page.locator('.access-add input[role=switch]').check();
    await page.getByTestId(TestIds.resourceAccess.save).click();
    await expect(page.getByTestId(TestIds.resourceAccess.saved)).toBeVisible();
    const resource = await pages.resourceAccess.identity();
    const saved = await page.request.get(`${product.consoleUrl}/api/resource-access-policies/${resource.uid}`, {
      headers: { 'X-Agentstration-Context': '/instance' },
    });
    expect(saved.status()).toBe(200);
    const policy = await saved.json();
    expect(policy.resource.version).toBeNull();
    expect(policy.grants).toEqual([{
      targetScopeRef: '/tenants/' + workspace.tenantId,
      actions: ['discover', 'use'], includeDescendants: true,
    }]);
    await expect(page.getByTestId(TestIds.resourceAccess.save)).toHaveText(
      locale === 'fr-FR' ? 'Enregistrer les accès' : 'Save access');
    const stored = await (await page.request.get(product.consoleUrl + '/api/identity/preferences')).json();
    expect(stored.language).toBeNull();
    expect((await context.cookies()).filter(cookie => cookie.name === '.AspNetCore.Culture')).toEqual([]);
    expect(errors).toEqual([]);
    results.push({ locale, language: 'automatic', source: 'Workspace', target: 'Instance',
      draftFlowAccessSaved: true, descendants: true, languagePreserved: true, noBrowserErrors: true });
    await context.close();
  }
  await fs.writeFile(path.join(evidence, 'flow-access-verification.json'), JSON.stringify(results, null, 2) + '\n');
  console.log(JSON.stringify(results, null, 2));
} finally {
  await browser.close();
  await product.stop();
}
