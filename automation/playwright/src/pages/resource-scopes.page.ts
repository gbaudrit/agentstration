import { expect, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { ResourceScopeTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';

export class ResourceScopesPage {
  public constructor(private readonly page: Page) {}

  public get inventory() { return this.page.getByTestId(TestIds.distribution.resourceScopes); }
  private get detail() { return this.inventory.locator('.scope-detail'); }
  private get rows() { return this.detail.locator('tr.resource-row'); }
  private get kinds() { return this.page.getByTestId(TestIds.resourceScopes.kind); }
  private get count() { return this.page.getByTestId(TestIds.resourceScopes.count); }
  private get search() { return this.detail.locator('input[type="search"]'); }

  public async open(consoleUrl: string, scopeRef?: string): Promise<void> {
    const shell = this.page.getByTestId(TestIds.console.shell);
    let scope = scopeRef;
    if (!scope) {
      await expect(async () => {
        const current = await shell.getAttribute('data-context-scope');
        expect(current).toMatch(/^\/(instance|tenants\/|workspaces\/)/);
        scope = current!;
      }).toPass();
    }
    if (!scope) throw new Error("The Console did not expose an active resource scope.");
    await this.page.goto(`${consoleUrl}/settings/resource-scopes?context=${encodeURIComponent(scope)}`);
    await expect(shell).toHaveAttribute('data-context-scope', scope);
    await expect(shell).toHaveAttribute('data-preferences-ready', 'true');
    await expect(this.count).toBeVisible();
  }

  public async selectedWorkspace(): Promise<{ id: string; name: string }> {
    const reference = this.detail.locator('.scope-reference');
    await expect(reference).toHaveText(/^\/workspaces\/[0-9a-f-]{36}$/i);
    const scopeRef = await reference.innerText();
    return {
      id: scopeRef.slice('/workspaces/'.length),
      name: await this.detail.locator('h2').innerText(),
    };
  }

  public async assertScopedRoot(kind: 'tenant' | 'workspace', scopeRef: string): Promise<void> {
    const nodes = this.inventory.locator('.scope-tree-node');
    await expect(this.inventory.locator('.scope-tree-node.scope-kind-instance')).toHaveCount(0);
    const root = this.inventory.locator(`.scope-tree-node.scope-kind-${kind}[aria-current="true"]`);
    await expect(root).toBeVisible();
    await expect(root).toHaveAttribute('style', '--scope-depth: 0');
    await expect(this.detail.locator('.scope-reference')).toHaveText(scopeRef);
    if (kind === 'workspace') {
      await expect(nodes).toHaveCount(1);
    } else {
      await expect(this.inventory.locator('.scope-tree-node.scope-kind-tenant')).toHaveCount(1);
      await expect.poll(() => this.inventory.locator('.scope-tree-node.scope-kind-workspace').count()).toBeGreaterThan(0);
    }
  }

  public async exerciseHierarchy(workspaceName: string): Promise<string> {
    let tenantScope = '';
    for (const kind of ['instance', 'tenant', 'workspace']) {
      const nodes = this.inventory.locator(`.scope-tree-node.scope-kind-${kind}`);
      const node = kind === 'workspace' ? nodes.filter({ has: this.page.getByText(workspaceName, { exact: true }) }) : nodes.first();
      const name = await node.locator('strong').innerText();
      await node.click();
      await expect(node).toHaveAttribute('aria-current', 'true');
      await expect(this.detail.getByRole('heading', { name, exact: true })).toBeVisible();
      await expect(this.detail.locator('.scope-reference')).toContainText(kind === 'instance' ? '/instance' : `/${kind}s/`);
      await expect(this.search).toHaveValue('');
      await expect(this.kinds).toHaveValue('');
      if (kind === 'tenant') tenantScope = await this.detail.locator('.scope-reference').innerText();
    }
    return tenantScope;
  }

  public async exerciseFilters(locale: SupportedTestLocale): Promise<void> {
    const text = ResourceScopeTextByLocale[locale];
    await expect.poll(() => this.rows.count()).toBeGreaterThan(1);
    const total = await this.rows.count();
    expect(total).toBeGreaterThan(1);
    await expect(this.count).toHaveText(text.count(total, total));
    const baseline = await this.rows.evaluateAll(rows => rows.map(row => ({
      name: row.querySelector('td:first-child a, td:first-child strong')!.textContent!.trim(),
      kind: row.querySelector('td:nth-child(2)')!.textContent!.trim(),
    })));
    const kinds = [...new Set(baseline.map(row => row.kind))];
    expect(kinds.length).toBeGreaterThan(1);
    await expect(this.kinds.locator('option')).toHaveCount(kinds.length + 1);
    await expect(this.kinds.locator('option').first()).toHaveText(text.allKinds(total));
    for (const kind of kinds) {
      await expect(this.kinds.locator('option', { hasText: `${kind} (` })).toHaveText(`${kind} (${baseline.filter(row => row.kind === kind).length})`);
    }
    const kind = kinds[0];
    const matches = baseline.filter(row => row.kind === kind);
    await this.kinds.selectOption(kind);
    await expect(this.rows).toHaveCount(matches.length);
    await expect(this.count).toHaveText(text.count(matches.length, total));
    const query = matches[0].name;
    await this.search.fill(query);
    const combined = matches.filter(row => row.name.toLowerCase().includes(query.toLowerCase()));
    await expect(this.rows).toHaveCount(combined.length);
    await expect(this.count).toHaveText(text.count(combined.length, total));
    for (const row of await this.rows.all()) await expect(row.locator('td').nth(1)).toHaveText(kind);
    await this.search.fill('fr816-no-matching-resource');
    await expect(this.rows).toHaveCount(0);
    await expect(this.count).toHaveText(text.count(0, total));
    await expect(this.detail.getByText(text.empty, { exact: true })).toBeVisible();
    await this.search.fill('');
    await expect(this.rows).toHaveCount(matches.length);
    await this.kinds.selectOption('');
    await expect(this.rows).toHaveCount(total);
    await expect(this.count).toHaveText(text.count(total, total));
    const nonLink = this.rows.filter({ has: this.page.locator('td:first-child strong') });
    expect(await nonLink.count()).toBeGreaterThan(0);
    for (const row of await nonLink.all()) await expect(row.getByRole('link')).toHaveCount(0);
    await expect(this.detail.locator('thead th')).toHaveCount(4);
    for (const row of await this.rows.all()) expect(await row.getByRole('link').count()).toBeLessThanOrEqual(1);
  }

  public async assertResponsiveStyles(): Promise<void> {
    await expect.poll(() => this.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    // These computed values prove the isolated bundle was applied, rather than only linked.
    await expect(this.inventory.locator('.scope-explorer')).toHaveCSS('display', 'grid');
    if (this.page.viewportSize()!.width <= 760) {
      await expect(this.inventory.locator('.scope-tree')).toHaveCSS('position', 'static');
      const tree = await this.inventory.locator('.scope-tree').boundingBox();
      const detail = await this.detail.boundingBox();
      expect(detail!.y).toBeGreaterThanOrEqual(tree!.y + tree!.height);
    }
    await this.kinds.scrollIntoViewIfNeeded();
    await expect(this.kinds).toBeInViewport();
  }

  public async openLinkedResource(): Promise<void> {
    const link = this.rows.locator('a.resource-name-link').first();
    await expect(link).toBeVisible();
    const destination = new URL((await link.getAttribute('href'))!, this.page.url());
    const context = new URL(this.page.url()).searchParams.get('context');
    await link.click();
    await expect(this.page).toHaveURL(url => url.pathname === destination.pathname
      && [...destination.searchParams].every(([key, value]) => url.searchParams.get(key) === value)
      && url.searchParams.get('context') === context);
    await expect(this.page.locator('h1').first()).toBeVisible();
  }
}
