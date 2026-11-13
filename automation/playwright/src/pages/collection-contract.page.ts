import { FlowCollectionPage } from './flow-collection.page.js';
import { ExecutionScopesPage } from './execution-scopes.page.js';
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
    if (['/resource-plans', '/workspaces', '/account/pat'].includes(url.pathname)) {
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
    if (new URL(this.page.url()).pathname === '/flows') {
      const root = this.page.getByTestId(TestIds.flowObservability.flows);
      await expect(root).toHaveAttribute('data-interactive', 'true');
      await expect(root).toHaveAttribute('aria-busy', 'false');
      if (await root.locator('.collection-identity').count()) await new FlowCollectionPage(this.page).assertCompactStructure(locale);
      else await expect(root.locator('.state-panel')).toBeVisible();
    }
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
        // Some rows have no applicable command even when the collection has an Actions column.
        if (await action.locator(':scope > *').count()) await expect(action).toBeVisible();
        // Business fields may contain links; the item identity remains in the first cell/card block.
        const firstIdentity = row.locator('.collection-identity-cell, :scope > .collection-identity').first();
        if (await firstIdentity.count()) expect((await firstIdentity.textContent())?.trim()).not.toBe('');
      }
    }
    for (const identity of await identities.all()) {
      await expect(identity.locator('.resource-icon')).toBeVisible();
      const primary = identity.locator('.resource-name-link, strong').first();
      await expect(primary).toBeVisible();
      for (const version of await identity.locator('.collection-version').all()) {
        await expect(version.locator('.collection-version-label')).toBeVisible();
        expect((await version.locator('.collection-version-label').textContent())?.trim()).not.toBe('');
        expect((await version.locator('code').textContent())?.trim()).not.toBe('');
        await expect(version.locator('xpath=ancestor::a')).toHaveCount(0);
        await expect(version.locator('xpath=ancestor::*[contains(@class, "collection-identity-text")]')).toHaveCount(1);
      }
      await expect(identity.locator('.collection-qualified-name > svg')).toHaveCount(0);
      await expect(identity.locator('.collection-version-draft, .collection-version-indicator')).toHaveCount(0);
      const code = identity.locator('.collection-qualified-name code');
      if (await code.count()) {
        expect(await code.textContent()).not.toMatch(/\s\/|\/\s/);
        await expect(identity.locator('.copy-value')).toHaveCount(1);
      }
    }
    for (const group of await this.page.locator('.collection-action-group:visible').all()) {
      const primary = group.locator('.collection-primary-actions');
      await expect(primary.locator('details')).toHaveCount(0);
      const commands = primary.locator('button, a');
      for (const command of await commands.all()) await expect(command).toBeVisible();
      if (!await group.locator('.collection-actions-panel').count()) await expect(group.locator('details')).toHaveCount(0);
    }
    for (const state of await states.all()) {
      await expect(state.locator('.ui-icon')).toBeVisible();
      expect((await state.textContent())?.trim()).not.toBe('');
      if ((await state.textContent())?.trim() === text.unknown) await expect(state).toHaveClass(/status-neutral/);
    }
    for (const scope of await scopes.all()) {
      if ((await scope.getAttribute('class'))?.split(/\s+/).includes('resource-owner-icon')) {
        const button = scope.getByRole('button');
        const name = await button.getAttribute('aria-label');
        expect(text.scopeTypes.some(type => name === type || name?.startsWith(`${type} — `))).toBe(true);
        await expect(button).toHaveAccessibleName(name!);
        await expect(scope.locator('[role=tooltip]')).toHaveText(name!);
        const letter = scope.locator('.scope-letter');
        await expect(letter).toHaveText(/^[ITW]$/);
        await expect(letter).toHaveAttribute('aria-hidden', 'true');
        const visual = await letter.boundingBox();
        const target = await button.boundingBox();
        expect(visual!.width).toBeGreaterThanOrEqual(10);
        expect(visual!.width).toBeLessThanOrEqual(12);
        expect(target!.width).toBeGreaterThanOrEqual(24);
        expect(target!.height).toBeGreaterThanOrEqual(24);
        continue;
      }
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
        await copy.focus();
        await expect(copy).toBeFocused();
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
    const owner = ('International research, development and operations — ' + 'long-tenant-name-'.repeat(6)).slice(0, 120);
    const tenant = await this.page.request.post(`${consoleUrl}/api/identity/tenants`, {
      headers: { 'X-Agentstration-Context': '/instance' },
      data: { name: `collection-${suffix}`, displayName: owner },
    });
    expect(tenant.status(), await tenant.text()).toBe(201);
    const scope = `/tenants/${(await tenant.json()).id}`;
    const namespaces = [`collection-${suffix}-long-qualified-resource-identity`, `collection-${suffix}-other`];
    const execution = new ExecutionScopesPage(this.page);
    await execution.ensureFixture(consoleUrl);
    const provider = await this.page.request.get(`${consoleUrl}/api/modelproviders/execution-scope-ollama?scopeRef=%2Finstance`, {
      headers: { 'X-Agentstration-Context': '/instance' },
    });
    expect(provider.ok(), await provider.text()).toBeTruthy();
    await execution.grantDescendantUse(consoleUrl, await provider.json(), scope);
    for (const namespace of namespaces) {
      const profile = await this.page.request.post(`${consoleUrl}/api/modelprofiles`, {
        headers: { 'X-Agentstration-Context': scope },
        data: { name: 'same-profile', namespace, scopeRef: scope, properties: {
          displayName: 'Shared profile', provider: { name: 'execution-scope-ollama', namespace: 'default', scopeRef: '/instance' },
          model: { name: 'qwen3:1.7b' }, generation: { temperature: 0 }, reasoning: { mode: 0 },
          output: { format: 0, strict: false }, providerOptions: {}, secretBindings: [],
        } },
      });
      expect(profile.status(), await profile.text()).toBe(201);
    }
    return { scope, owner, namespaces };
  }

  public async inspectLongOwnerHomonyms(consoleUrl: string, fixture: { scope: string; owner: string; namespaces: string[] }, touch: boolean): Promise<void> {
    const open = async () => {
      expect((await this.page.goto(`${consoleUrl}/modelprofiles?context=${encodeURIComponent(fixture.scope)}`))?.ok()).toBeTruthy();
      await expect(this.page.locator('.collection-identity').filter({ hasText: 'same-profile' })).toHaveCount(2);
    };
    await this.page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
    await open();
    const row = this.page.locator('.resource-collection tbody > tr, .collection-card').filter({ hasText: `${fixture.namespaces[0]}/same-profile` });
    const scope = row.locator('.resource-owner-scope');
    const disclosure = scope.locator('.disclosure-trigger');
    const fullName = `${CollectionTextByLocale['en-US'].scopeTypes[1]} — ${fixture.owner}`;
    await expect(scope.locator('.scope-letter')).toHaveText('T');
    await expect(disclosure).toHaveAccessibleName(fullName);
    await expect(disclosure).toHaveAttribute('title', fullName);
    const marker = await scope.locator('.scope-letter').boundingBox();
    expect(marker!.width).toBeLessThanOrEqual(12);
    await disclosure.focus();
    await expect(this.page.locator('.full-value-panel:popover-open')).toHaveText(fullName);
    await this.page.keyboard.press('Escape');
    await expect(disclosure).toBeFocused();
    await expect(this.page.locator('.full-value-panel:popover-open')).toHaveCount(0);
    await disclosure.hover();
    await expect(this.page.locator('.full-value-panel:popover-open')).toHaveText(fullName);
    await this.page.keyboard.press('Escape');
    if (touch) {
      await disclosure.tap();
      await expect(this.page.locator('.full-value-panel:popover-open')).toHaveText(fullName);
      await this.page.keyboard.press('Escape');
    }
    const requests: string[] = [];
    const track = (request: import('@playwright/test').Request) => {
      if (request.method() === 'GET' && /\/api\/identity\/(tenants|workspaces)/.test(request.url())) requests.push(request.url());
    };
    this.page.on('request', track);
    try {
      for (const namespace of fixture.namespaces) {
        const identity = this.page.locator('.collection-identity').filter({ hasText: `${namespace}/same-profile` });
        const link = identity.locator('.resource-name-link');
        const url = new URL((await link.getAttribute('href'))!, consoleUrl);
        expect(url.searchParams.get('namespace')).toBe(namespace);
        expect(url.searchParams.get('scopeRef')).toBe(fixture.scope);
        expect(url.searchParams.get('context')).toBe(fixture.scope);
        const before = this.page.url();
        await identity.locator('.copy-value').focus();
        await identity.locator('.copy-value').click();
        await expect.poll(() => this.page.evaluate(() => navigator.clipboard.readText())).toBe(`${namespace}/same-profile`);
        expect(this.page.url()).toBe(before);
      }
      await this.inspectMenu(row.locator('.collection-actions-menu'));
      await disclosure.focus();
      await this.page.keyboard.press('Escape');
      expect(requests).toEqual([]);
    } finally { this.page.off('request', track); }
    for (const namespace of fixture.namespaces) {
      await this.page.locator('.collection-identity').filter({ hasText: `${namespace}/same-profile` }).locator('.resource-name-link').click();
      await expect(this.page.getByTestId(TestIds.resourceAdministration.modelProfileEditor)).toBeVisible();
      expect(new URL(this.page.url()).searchParams.get('namespace')).toBe(namespace);
      const overview = this.page.getByTestId(TestIds.collections.modelProfileOverview);
      await expect(overview).toContainText('Shared profile');
      await expect(overview.getByText(namespace, { exact: true })).toBeVisible();
      await open();
    }
  }

  public async showOwnerDisclosure(owner: string, touch: boolean): Promise<void> {
    const trigger = this.page.getByRole('button', { name: `${CollectionTextByLocale['en-US'].scopeTypes[1]} — ${owner}`, exact: true }).first();
    if (touch) await trigger.tap(); else await trigger.focus();
    await expect(this.page.locator('.full-value-panel:popover-open')).toHaveText(`${CollectionTextByLocale['en-US'].scopeTypes[1]} — ${owner}`);
  }

  public async inspectMenu(menu: Locator): Promise<void> {
    const trigger = menu.locator(':scope > summary');
    const before = this.page.url();
    await expect(trigger).toHaveAccessibleName(/.+/);
    await trigger.focus();
    await trigger.press('ArrowDown');
    await expect(menu).toHaveAttribute('open', '');
    const items = menu.locator('a[href]:visible, button:enabled:visible, input:enabled:visible, select:enabled:visible');
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
