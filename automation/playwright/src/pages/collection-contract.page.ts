import { randomUUID } from 'node:crypto';
import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { CollectionTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';

export interface CollectionObservation {
  url: string;
  frames: number;
  identities: number;
  menus: number;
  scopes: number;
  states: number;
}

/** Applicable presentation assertions; family page objects still own commands and data. */
export class CollectionContractPage {
  public constructor(private readonly page: Page) {}

  public async open(consoleUrl: string, route: string): Promise<void> {
    const url = new URL(route, consoleUrl);
    if (['/resource-plans', '/workspaces'].includes(url.pathname)) {
      const response = await this.page.request.get(`${consoleUrl}/api/identity/context`);
      expect(response.ok()).toBeTruthy();
      const identity = await response.json() as { availableWorkspaces: Array<{ id: string; name: string }> };
      const workspace = identity.availableWorkspaces.find(value => value.name === 'default');
      if (!workspace) throw new Error('The collection campaign requires the prepared default Workspace.');
      url.searchParams.set('context', `/workspaces/${workspace.id}`);
    } else url.searchParams.set('context', '/instance');
    expect((await this.page.goto(url.href))?.ok(), route).toBeTruthy();
    await expect(this.page.locator('main')).toBeVisible();
    await expect(this.page.locator('main .state-panel .spinner')).toHaveCount(0);
  }

  public async inspect(locale: SupportedTestLocale, interactions = true): Promise<CollectionObservation> {
    const text = CollectionTextByLocale[locale];
    const frames = this.page.locator('.resource-collection:visible');
    const identities = this.page.locator('.collection-identity:visible');
    const menus = this.page.locator('.collection-actions-menu:visible');
    const scopes = this.page.locator('.resource-owner-scope:visible');
    const states = this.page.locator('.status-badge:visible');
    for (const frame of await frames.all()) {
      const headings = frame.locator('thead th');
      if (await headings.count()) {
        const values = await headings.allTextContents();
        const scope = values.indexOf(text.scope);
        const state = values.indexOf(text.state);
        const actions = values.indexOf(text.actions);
        if (scope >= 0) expect(scope).toBe(1);
        if (state >= 0 && scope >= 0) expect(state).toBe(scope + 1);
        if (actions >= 0) expect(actions).toBe(values.length - 1);
      }
      for (const row of await frame.locator('tbody > tr:not(.collection-expanded), .collection-card').all()) {
        const action = row.locator('.collection-actions');
        if (await action.count()) await expect(action).toBeVisible();
        // Business fields may contain links; the item identity remains in the first cell/card block.
        const firstIdentity = row.locator('.collection-identity-cell, :scope > .collection-identity').first();
        if (await firstIdentity.count()) expect((await firstIdentity.textContent())?.trim()).not.toBe('');
      }
    }
    for (const identity of await identities.all()) {
      await expect(identity.locator('.resource-icon')).toBeVisible();
      const primary = identity.locator('.resource-name-link, strong').first();
      await expect(primary).toBeVisible();
      const code = identity.locator('.collection-qualified-name code');
      if (await code.count()) {
        expect(await code.textContent()).not.toMatch(/\s\/|\/\s/);
        await expect(identity.locator('.copy-value')).toHaveCount(1);
      }
    }
    for (const state of await states.all()) {
      await expect(state.locator('.ui-icon')).toBeVisible();
      expect((await state.textContent())?.trim()).not.toBe('');
      if ((await state.textContent())?.trim() === text.unknown) await expect(state).toHaveClass(/status-neutral/);
    }
    for (const scope of await scopes.all()) {
      const kind = scope.locator('.resource-scope-badge');
      await expect(kind).toBeVisible();
      expect(text.scopeTypes).toContain((await kind.textContent())?.trim());
      const disclosure = scope.locator('.disclosure-trigger');
      if (await disclosure.count()) {
        expect((await disclosure.textContent())?.trim()).not.toBe('');
        await expect(disclosure).toHaveAccessibleName((await disclosure.textContent())!.trim());
      }
    }
    if (interactions) {
      const copy = identities.locator('.copy-value').first();
      if (await copy.count()) {
        const value = await copy.locator('xpath=..').locator('code').textContent();
        const before = this.page.url();
        await copy.click();
        await expect.poll(() => this.page.evaluate(() => navigator.clipboard.readText())).toBe(value);
        expect(this.page.url()).toBe(before);
      }
      if (await menus.count()) await this.inspectMenu(menus.first());
    }
    expect(await this.page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(0);
    return { url: this.page.url(), frames: await frames.count(), identities: await identities.count(), menus: await menus.count(), scopes: await scopes.count(), states: await states.count() };
  }

  public async prepareLongOwnerHomonyms(consoleUrl: string): Promise<{ scope: string; owner: string; namespaces: string[] }> {
    const suffix = randomUUID().slice(0, 8);
    const owner = 'International research, development and operations — ' + 'long-tenant-name-'.repeat(6);
    const tenant = await this.page.request.post(`${consoleUrl}/api/identity/tenants`, {
      headers: { 'X-Agentstration-Context': '/instance' },
      data: { name: `collection-${suffix}`, displayName: owner },
    });
    expect(tenant.status(), await tenant.text()).toBe(201);
    const scope = `/tenants/${(await tenant.json()).id}`;
    const namespaces = [`collection-${suffix}-long-qualified-resource-identity`, `collection-${suffix}-other`];
    for (const namespace of namespaces) {
      const category = await this.page.request.post(`${consoleUrl}/api/toolcategories`, {
        headers: { 'X-Agentstration-Context': scope },
        data: { name: 'same-category', namespace, scopeRef: scope, properties: { displayName: 'Shared category', tools: [] } },
      });
      expect(category.status(), await category.text()).toBe(201);
    }
    return { scope, owner, namespaces };
  }

  public async inspectLongOwnerHomonyms(consoleUrl: string, fixture: { scope: string; owner: string; namespaces: string[] }, touch: boolean): Promise<void> {
    const open = async () => {
      expect((await this.page.goto(`${consoleUrl}/tools/categories?context=${encodeURIComponent(fixture.scope)}`))?.ok()).toBeTruthy();
      await expect(this.page.locator('.collection-identity')).toHaveCount(2);
    };
    await this.page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
    await open();
    const scope = this.page.locator('.resource-owner-scope').first();
    const disclosure = scope.locator('.disclosure-trigger');
    await expect(scope.locator('.resource-scope-badge')).toBeVisible();
    await expect(disclosure).toHaveAccessibleName(fixture.owner);
    await expect(disclosure).toHaveText(fixture.owner);
    expect(await disclosure.evaluate(element => element.scrollWidth > element.clientWidth)).toBeTruthy();
    await disclosure.focus();
    await expect(this.page.locator('.full-value-panel:popover-open')).toHaveText(fixture.owner);
    await this.page.keyboard.press('Escape');
    await expect(disclosure).toBeFocused();
    await expect(this.page.locator('.full-value-panel:popover-open')).toHaveCount(0);
    await disclosure.hover();
    await expect(this.page.locator('.full-value-panel:popover-open')).toHaveText(fixture.owner);
    await this.page.keyboard.press('Escape');
    if (touch) {
      await disclosure.tap();
      await expect(this.page.locator('.full-value-panel:popover-open')).toHaveText(fixture.owner);
      await this.page.keyboard.press('Escape');
    }
    const requests: string[] = [];
    const track = (request: import('@playwright/test').Request) => {
      if (request.method() === 'GET' && /\/api\/identity\/(tenants|workspaces)/.test(request.url())) requests.push(request.url());
    };
    this.page.on('request', track);
    try {
      for (const namespace of fixture.namespaces) {
        const identity = this.page.locator('.collection-identity').filter({ hasText: `${namespace}/same-category` });
        const link = identity.locator('.resource-name-link');
        const url = new URL((await link.getAttribute('href'))!, consoleUrl);
        expect(url.searchParams.get('namespace')).toBe(namespace);
        expect(url.searchParams.get('scopeRef')).toBe(fixture.scope);
        expect(url.searchParams.get('context')).toBe(fixture.scope);
        const before = this.page.url();
        await identity.locator('.copy-value').click();
        await expect.poll(() => this.page.evaluate(() => navigator.clipboard.readText())).toBe(`${namespace}/same-category`);
        expect(this.page.url()).toBe(before);
      }
      await this.inspectMenu(this.page.locator('.collection-actions-menu').first());
      await disclosure.focus();
      await this.page.keyboard.press('Escape');
      expect(requests).toEqual([]);
    } finally { this.page.off('request', track); }
    for (const namespace of fixture.namespaces) {
      await this.page.locator('.collection-identity').filter({ hasText: `${namespace}/same-category` }).locator('.resource-name-link').click();
      await expect(this.page.getByTestId(TestIds.toolCategories.editor)).toBeVisible();
      expect(new URL(this.page.url()).searchParams.get('namespace')).toBe(namespace);
      await expect(this.page.getByTestId(TestIds.toolCategories.displayName)).toHaveValue('Shared category');
      await open();
    }
  }

  public async inspectMenu(menu: Locator): Promise<void> {
    const trigger = menu.locator(':scope > summary');
    const before = this.page.url();
    await expect(trigger).toHaveAccessibleName(/.+/);
    await trigger.focus();
    await trigger.press('ArrowDown');
    await expect(menu).toHaveAttribute('open', '');
    const items = menu.locator('a[href], button:enabled, input:enabled, select:enabled');
    expect(await menu.locator('a[href], button, input, select').count()).toBeGreaterThan(0);
    if (await items.count()) {
      await expect(items.first()).toBeFocused();
      await this.page.keyboard.press('End');
      await expect(items.last()).toBeFocused();
      await this.page.keyboard.press('Home');
      await expect(items.first()).toBeFocused();
    }
    await this.page.keyboard.press('Escape');
    await expect(trigger).toBeFocused();
    await expect(menu).not.toHaveAttribute('open', '');
    expect(this.page.url()).toBe(before);
  }
}
