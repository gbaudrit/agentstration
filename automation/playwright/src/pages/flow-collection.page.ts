import { randomUUID } from 'node:crypto';
import { expect, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { FlowCollectionTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';

export class FlowCollectionPage {
  private contextScope: string | null = null;
  public constructor(private readonly page: Page) {}
  private headers(): Record<string, string> { return this.contextScope ? { 'X-Agentstration-Context': this.contextScope } : {}; }
  private root() { return this.page.getByTestId(TestIds.flowObservability.flows); }
  private rows() { return this.root().locator('tbody tr, .collection-card'); }

  public async assertCompactStructure(locale: SupportedTestLocale): Promise<void> {
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-preferences-ready', 'true');
    await expect(this.root()).toHaveAttribute('data-interactive', 'true');
    await expect(this.root()).toHaveAttribute('aria-busy', 'false');
    const row = this.rows().first();
    await expect(row.locator('.collection-identity a.resource-name-link')).toBeVisible();
    await expect(row.locator('.flow-versions .collection-version-compact').first()).toBeVisible();
    await expect(row.locator('.collection-qualified-name > svg')).toHaveCount(0);
    await expect(row.locator('.resource-owner-icon button')).toHaveAccessibleName(/.+/);
    const scopeIcon = await row.locator('.resource-owner-icon svg').boundingBox();
    expect(scopeIcon!.width).toBeGreaterThanOrEqual(24);
    expect(scopeIcon!.height).toBeGreaterThanOrEqual(24);
    await expect(row.locator('.status-icon-only button')).toHaveAccessibleName(/.+/);
    await expect(row.locator('.collection-identity-versions')).toHaveCount(0);
    const qualified = await row.locator('.collection-qualified-name code').textContent();
    expect(qualified).toMatch(/^[^\s]+\/[^\s]+$/);
    if (await this.root().locator('table').isVisible()) {
      const text = FlowCollectionTextByLocale[locale];
      await expect(this.root().getByRole('columnheader')).toHaveCount(4);
      await expect(this.root().getByRole('columnheader').nth(0)).toHaveText(text.resource);
      await expect(this.root().getByRole('columnheader').nth(1)).toContainText(text.version);
      await expect(this.root().getByRole('columnheader').nth(2)).toHaveText(text.updated);
      await expect(this.root().getByRole('columnheader').nth(3)).toHaveText(text.actions);
    }
    expect(await this.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  }

  public async assertManagedPreview(): Promise<void> {
    await expect(this.root().locator('.managed-icon-only button').first()).toBeVisible();
    await expect(this.root().locator('.managed-icon-only button').first()).toHaveAccessibleName(/.+/);
    const row = this.rows().filter({ has: this.page.locator('.managed-icon-only') }).first();
    const icon = await row.locator('.collection-family-icon > .resource-icon').boundingBox();
    const state = await row.locator('.status-icon-only svg').boundingBox();
    const lock = await row.locator('.managed-icon-only svg').boundingBox();
    expect(Math.abs(state!.x + state!.width / 2 - (icon!.x + icon!.width))).toBeLessThan(1);
    expect(Math.abs(state!.y + state!.height / 2 - icon!.y)).toBeLessThan(1);
    expect(Math.abs(lock!.x + lock!.width / 2 - (icon!.x + icon!.width))).toBeLessThan(1);
    expect(Math.abs(lock!.y + lock!.height / 2 - (icon!.y + icon!.height))).toBeLessThan(1);
  }

  public async seed(consoleUrl: string): Promise<string[]> {
    this.contextScope = await this.page.getByTestId(TestIds.console.shell).getAttribute('data-context-scope');
    const prefix = `000-compact-${randomUUID().slice(0, 8)}`;
    const ids: string[] = [];
    for (let index = 0; index < 22; index++) {
      const id = `${prefix}-${String(index).padStart(2, '0')}`;
      const response = await this.page.request.post(`${consoleUrl}/api/flows/drafts`, { headers: this.headers(), data: { name: id, displayName: index === 0 ? 'Compact published Flow with draft' : `Compact Flow ${index}`, template: 'Empty' } });
      expect(response.status(), await response.text()).toBe(201);
      ids.push(id);
    }
    const published = await this.page.request.post(`${consoleUrl}/api/flows/${ids[0]}/publish`, { headers: this.headers(), data: { version: '1.0.0', activate: true } });
    expect(published.status(), await published.text()).toBe(201);
    const draft = await this.page.request.get(`${consoleUrl}/api/flows/${ids[0]}/draft`, { headers: this.headers() });
    expect(draft.ok()).toBe(true);
    expect((await draft.json() as { value: { revision: number } }).value.revision).toBe(1);
    return ids;
  }

  public async inspectInheritedFlow(consoleUrl: string, id: string, locale: SupportedTestLocale): Promise<void> {
    const identity = await this.page.request.get(`${consoleUrl}/api/identity/context`);
    expect(identity.ok()).toBe(true);
    const workspaces = (await identity.json() as { availableWorkspaces: { id: string; name: string }[] }).availableWorkspaces;
    const workspace = workspaces.find(value => value.name === 'default');
    expect(workspace, 'The existing default Workspace is available to the fixture administrator.').toBeTruthy();
    const scope = `/workspaces/${workspace!.id}`;
    const inventory = await this.page.request.get(`${consoleUrl}/api/resource-scopes`, { headers: this.headers() });
    expect(inventory.ok()).toBe(true);
    const body = await inventory.json() as { scopes: { scopeRef: string; resources: { uid: string; namespace: string; kind: string; name: string }[] }[] };
    const resource = body.scopes.find(value => value.scopeRef === '/instance')?.resources.find(value => value.kind === 'Flow' && value.namespace === 'default' && value.name === id);
    expect(resource).toBeTruthy();
    const policyUrl = `${consoleUrl}/api/resource-access-policies/${resource!.uid}`;
    const current = await this.page.request.get(policyUrl, { headers: this.headers() });
    expect([200, 404]).toContain(current.status());
    const grants = current.status() === 200 ? (await current.json() as { grants: { targetScopeRef: string; actions: string[]; includeDescendants: boolean }[] }).grants : [];
    const policy = await this.page.request.put(policyUrl, {
      headers: { ...this.headers(), ...(current.headers()['etag'] ? { 'If-Match': current.headers()['etag']! } : {}) }, data: {
        resource: { uid: resource!.uid, ownerScopeRef: '/instance', namespace: 'default', kind: 'Flow' },
        grants: [...grants.filter(value => value.targetScopeRef !== scope), { targetScopeRef: scope, actions: ['discover', 'use'], includeDescendants: false }],
      },
    });
    expect(policy.status(), await policy.text()).toBe(200);
    const visible = await this.page.request.get(`${consoleUrl}/api/flows?allNamespaces=true&top=20`, { headers: { 'X-Agentstration-Context': scope } });
    expect(visible.ok(), await visible.text()).toBe(true);
    expect((await visible.json() as { value: { id: string; scopeRef: string }[] }).value).toContainEqual(expect.objectContaining({ id, scopeRef: '/instance' }));
    const destination = `${consoleUrl}/flows?context=${encodeURIComponent(scope)}`;
    await this.page.goto(destination, { waitUntil: 'domcontentloaded' });
    await this.assertCompactStructure(locale);
    const row = this.rows().filter({ has: this.page.locator(`a.resource-name-link[href*="/${id}?"]`) });
    await expect(row).toHaveCount(1);
    const lock = row.locator('.managed-icon-only button');
    await expect(lock).toHaveAccessibleName(FlowCollectionTextByLocale[locale].inheritedReadOnly);
    await lock.focus();
    await expect(row.locator('.managed-icon-only [role=tooltip]')).toBeVisible();
    await lock.press('Escape');
    await row.locator('a.resource-name-link').click();
    await expect(this.page.getByTestId(TestIds.common.inheritedResourceNotice)).toBeVisible();
    await this.page.goto(destination, { waitUntil: 'domcontentloaded' });
    await this.assertCompactStructure(locale);
  }

  public async remove(consoleUrl: string, ids: string[]): Promise<void> {
    for (const id of ids) {
      const response = await this.page.request.delete(`${consoleUrl}/api/flows/${id}${this.contextScope ? `?scopeRef=${encodeURIComponent(this.contextScope)}` : ''}`, { headers: this.headers() });
      expect(response.status(), await response.text()).toBe(204);
    }
  }

  public async exercisePagination(consoleUrl: string, locale: SupportedTestLocale): Promise<void> {
    const text = FlowCollectionTextByLocale[locale];
    const expected = async (skip: number, top: number) => {
      const response = await this.page.request.get(`${consoleUrl}/api/flows?allNamespaces=true&skip=${skip}&top=${top}`, { headers: this.headers() });
      expect(response.ok()).toBe(true);
      const body = await response.json() as { value: { namespace: string; id: string }[]; nextLink: string | null };
      return { identities: body.value.map(item => `${item.namespace}/${item.id}`), hasMore: Boolean(body.nextLink) };
    };
    await this.assertCompactStructure(locale);
    const first = await expected(0, 20);
    await expect(this.rows().locator('.collection-qualified-name code')).toHaveText(first.identities);
    expect(first.hasMore).toBe(true);
    const pager = this.root().locator('.collection-pager');
    await expect(pager.getByRole('button', { name: text.previous, exact: true })).toBeDisabled();
    await pager.getByRole('button', { name: text.next, exact: true }).click();
    await expect(pager.getByText(text.pageTwo, { exact: true })).toBeVisible();
    const second = await expected(20, 20);
    await expect(this.rows().locator('.collection-qualified-name code')).toHaveText(second.identities);
    if (second.hasMore) await expect(pager.getByRole('button', { name: text.next, exact: true })).toBeEnabled();
    else await expect(pager.getByRole('button', { name: text.next, exact: true })).toBeDisabled();
    await this.root().getByLabel(text.pageSize, { exact: true }).selectOption('50');
    await expect(pager.getByText(text.pageOne, { exact: true })).toBeVisible();
    const larger = await expected(0, 50);
    await expect(this.rows().locator('.collection-qualified-name code')).toHaveText(larger.identities);
    if (larger.hasMore) await expect(pager.getByRole('button', { name: text.next, exact: true })).toBeEnabled();
    else await expect(pager.getByRole('button', { name: text.next, exact: true })).toBeDisabled();
    await expect(pager.locator('[role=status]')).toHaveCount(0);
  }

  public async assertVersionAlignmentAndDisclosure(id: string, locale: SupportedTestLocale, touch = false): Promise<void> {
    const row = this.rows().filter({ has: this.page.locator(`a.resource-name-link[href*="/${id}?"]`) });
    await expect(row).toHaveCount(1);
    await expect(row.locator('.managed-icon-only')).toHaveCount(0);
    const versions = row.locator('.collection-version-compact');
    await expect(versions).toHaveCount(2);
    await expect(versions.nth(0).locator('code')).toHaveText('1.0.0');
    await expect(versions.nth(1).locator('code')).toHaveText('r1');
    const boxes = await versions.locator('code').evaluateAll(nodes => nodes.map(node => node.getBoundingClientRect().x));
    expect(Math.abs(boxes[0] - boxes[1])).toBeLessThan(1);
    const pencil = versions.nth(1).getByRole('button', { name: FlowCollectionTextByLocale[locale].draft, exact: true });
    const number = await versions.nth(1).locator('code').boundingBox();
    const marker = await pencil.boundingBox();
    expect(marker!.x).toBeGreaterThanOrEqual(number!.x + number!.width);
    expect(marker!.x - number!.x - number!.width).toBeLessThan(4);
    await pencil.focus();
    await expect(versions.nth(1).getByRole('tooltip')).toBeVisible();
    await pencil.press('Escape');
    await expect(versions.nth(1).getByRole('tooltip')).toBeHidden();
    const scope = row.locator('.resource-owner-icon button');
    await scope.hover();
    await expect(row.locator('.resource-owner-icon [role=tooltip]')).toBeVisible();
    await this.page.mouse.move(0, 0);
    await scope.focus();
    await expect(row.locator('.resource-owner-icon [role=tooltip]')).toBeVisible();
    await scope.press('Escape');
    if (touch) await scope.tap(); else await scope.click();
    await expect(row.locator('.resource-owner-icon [role=tooltip]')).toBeVisible();
    await scope.press('Escape');
    const before = this.page.url();
    const copy = row.locator('.copy-value');
    const qualified = row.locator('.collection-qualified-name code');
    await this.root().getByLabel(FlowCollectionTextByLocale[locale].pageSize, { exact: true }).focus();
    await this.page.mouse.move(0, 0);
    await expect(copy).toHaveCSS('opacity', touch ? '1' : '0');
    await row.hover();
    await expect(copy).toHaveCSS('opacity', '1');
    const identity = await qualified.boundingBox();
    const copyBox = await copy.boundingBox();
    expect(copyBox!.x - identity!.x - identity!.width).toBeGreaterThanOrEqual(0);
    expect(copyBox!.x - identity!.x - identity!.width).toBeLessThan(4);
    await this.page.mouse.move(0, 0);
    await qualified.focus();
    await expect(copy).toHaveCSS('opacity', '1');
    await copy.click();
    await expect.poll(() => this.page.evaluate(() => navigator.clipboard.readText())).toBe(`default/${id}`);
    expect(this.page.url()).toBe(before);
    const menu = row.locator('.collection-actions-menu summary');
    await menu.focus();
    await menu.press('ArrowDown');
    await expect(row.locator('.collection-actions-panel a').first()).toBeFocused();
    await this.page.keyboard.press('Escape');
    await expect(menu).toBeFocused();
    expect(this.page.url()).toBe(before);
    const link = row.locator('a.resource-name-link');
    const destination = new URL((await link.getAttribute('href'))!, before).href;
    await link.click();
    await expect(this.page).toHaveURL(destination);
    await expect(this.page.getByTestId(TestIds.flowObservability.flowDetails)).toBeVisible();
    await this.page.goto(before, { waitUntil: 'domcontentloaded' });
    await this.assertCompactStructure(locale);
  }

  public async table(): Promise<void> {
    await this.root().locator('.collection-view-switch button').nth(0).click();
    await expect(this.root().locator('table')).toBeVisible();
  }
  public async cards(): Promise<void> {
    await this.root().locator('.collection-view-switch button').nth(1).click();
    await expect(this.root().locator('.collection-card').first()).toBeVisible();
  }
  public async capture(path: string): Promise<void> {
    await this.page.mouse.move(0, 0);
    await this.page.screenshot({ path, fullPage: false });
  }
}
