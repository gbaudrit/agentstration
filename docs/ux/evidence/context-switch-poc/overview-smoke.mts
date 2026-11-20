import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, expect } from '../../../../automation/playwright/node_modules/@playwright/test/index.mjs';
import { startProductHosts } from '../../../../automation/playwright/src/fixtures/product-hosts.ts';
import { ProductPages } from '../../../../automation/playwright/src/pages/product.pages.ts';
import { selectResourceSection } from '../../../../automation/playwright/src/pages/resource-sections.ts';
import { TestIds } from '../../../../automation/playwright/src/contracts/test-ids.ts';

// Temporary executable evidence; the reusable UX scenario remains tracked in #1255.
const evidence = path.dirname(fileURLToPath(import.meta.url));
const product = await startProductHosts('Local');
const browser = await chromium.launch({
  executablePath: process.env.AGENTSTRATION_CHROMIUM_EXECUTABLE ?? '/usr/bin/chromium',
  headless: true, args: ['--no-sandbox'],
});
const results: object[] = [];
const flowName = 'overview-switch-draft';
try {
  for (const mobile of [false, true]) {
    const context = await browser.newContext({ locale: 'fr-FR',
      viewport: mobile ? { width: 390, height: 844 } : { width: 1440, height: 1000 } });
    const page = await context.newPage();
    const pages = new ProductPages(page);
    const errors: string[] = [];
    page.on('pageerror', error => errors.push(error.message));
    await pages.login.signIn(product.consoleUrl);
    const fixture = await pages.executionScopes.ensureFixture(product.consoleUrl);
    if (!mobile) {
      await page.goto(product.consoleUrl + '/flows/new?context=%2Finstance');
      await page.locator('[data-testid=flow-create-form][data-interactive=true]').waitFor();
      await page.getByTestId('flow-display-name').fill('Overview switch draft');
      await page.getByTestId('flow-display-name').blur();
      await page.getByTestId('flow-name').fill(flowName);
      await page.getByTestId('flow-name').blur();
      await page.getByTestId(TestIds.creationWizard.next).click();
      await page.getByTestId(TestIds.creationWizard.next).click();
      await page.getByTestId('flow-create').click();
      await page.waitForURL(url => url.pathname.startsWith('/flows/' + flowName));
    }
    const shell = page.getByTestId(TestIds.console.shell);
    async function checkOverview(scope: string) {
      await expect(shell).toHaveAttribute('data-context-scope', scope);
      await expect(shell).toHaveAttribute('data-preferences-ready', 'true');
      await expect(page.getByTestId('platform-overview')).toHaveAttribute('aria-busy', 'false');
      const counts: Record<string, number> = {};
      for (const family of ['agents', 'flows']) {
        const response = await page.request.get(`${product.consoleUrl}/api/${family}?allNamespaces=true&top=${family === 'agents' ? 1000 : 100}`, {
          headers: { 'X-Agentstration-Context': scope },
        });
        expect(response.status()).toBe(200);
        counts[family] = (await response.json()).value.length;
        await expect(page.locator(`.overview-metric-groups .metric-card[href="/${family}"] strong`)).toHaveText(String(counts[family]));
      }
      expect(await page.evaluate(() => (window as any).__overviewDocument)).toBe('retained');
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
      return counts;
    }
    async function switchTo(scope: string) {
      await page.getByTestId(TestIds.console.contextSelectorTrigger).click();
      await (scope === '/instance' ? page.getByTestId('context-option-instance')
        : page.locator(`[data-context-scope-ref="${scope}"]`)).click();
      return checkOverview(scope);
    }
    await page.goto(product.consoleUrl + '/?context=' + encodeURIComponent(fixture.scopes.workspace));
    await page.evaluate(() => { (window as any).__overviewDocument = 'retained'; });
    const workspace = await checkOverview(fixture.scopes.workspace);
    const instance = await switchTo('/instance');
    expect(instance.flows).not.toBe(workspace.flows);
    await page.screenshot({ path: path.join(evidence, mobile ? 'overview-mobile-instance.png' : 'overview-desktop-instance.png'), fullPage: true });

    // Navigate through the overview tile, then the collection, without reloading.
    await page.locator('.overview-metric-groups .metric-card[href="/flows"]').click();
    await expect(page.getByTestId('flow-observability-flows')).toHaveAttribute('data-interactive', 'true');
    await expect(page.getByTestId('flow-observability-flows')).toHaveAttribute('aria-busy', 'false');
    expect(new URL(page.url()).searchParams.get('context')).toBe('/instance');
    await page.locator(`a[href*="/flows/${flowName}?"]`).click();
    await selectResourceSection(page, 'definition/identity');
    await expect(page.getByTestId('flow-definition-display-name')).toBeEnabled();
    await expect(page.getByTestId('inherited-resource-notice')).toHaveCount(0);
    await selectResourceSection(page, 'access');
    await expect(page.getByTestId(TestIds.resourceAccess.openAdd)).toBeEnabled();
    if (!mobile) {
      await page.getByTestId(TestIds.resourceAccess.openAdd).click();
      await page.getByTestId(TestIds.resourceAccess.target).selectOption(fixture.scopes.tenant);
      await page.locator('.access-add input[role=switch]').check();
      await page.getByTestId(TestIds.resourceAccess.save).click();
      await expect(page.getByTestId(TestIds.resourceAccess.saved)).toBeVisible();
    }
    expect(await page.evaluate(() => (window as any).__overviewDocument)).toBe('retained');
    const sidebarToggle = page.getByTestId('console-sidebar-toggle');
    if (mobile && await sidebarToggle.getAttribute('aria-expanded') !== 'true') await sidebarToggle.click();
    await page.locator('.sidebar nav a[href="/"]').click();
    await checkOverview('/instance');
    const returned = await switchTo(fixture.scopes.workspace);
    if (!mobile) expect(returned.flows).toBe(workspace.flows + 1);
    await switchTo('/instance');
    await page.goBack();
    await checkOverview(fixture.scopes.workspace);
    await page.goForward();
    await checkOverview('/instance');
    expect(errors).toEqual([]);
    results.push({ viewport: mobile ? 'mobile' : 'desktop', workspace, instance, returned,
      countersMatchApi: true, noDocumentReload: true, instanceFlowEditable: true,
      historyPreservesContext: true, noBrowserErrors: true });
    await context.close();
  }
  await fs.writeFile(path.join(evidence, 'overview-verification.json'), JSON.stringify(results, null, 2) + '\n');
  console.log(JSON.stringify(results, null, 2));
} finally {
  await browser.close();
  await product.stop();
}
