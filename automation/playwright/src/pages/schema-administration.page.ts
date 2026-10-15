import { expect, request as playwrightRequest, type APIResponse, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

interface SchemaWire {
  definition: {
    displayName: string; description?: string; format: string; dialect?: string;
    content: Record<string, unknown>; usePolicy?: Record<string, unknown>;
  };
}

export interface SchemaAdministrationFixture {
  name: string; namespace: string; scopeRef: string; version: string;
}

const dialect = 'https://json-schema.org/draft/2020-12/schema';

export class SchemaAdministrationPage {
  public lastFixture?: SchemaAdministrationFixture;
  public constructor(private readonly page: Page) {}

  public async exercise(consoleUrl: string): Promise<SchemaAdministrationFixture> {
    const suffix = Date.now();
    const displayName = `Browser contract ${suffix}`;
    const name = `browser-contract-${suffix}`;
    const namespace = `ux-${suffix}`;
    await this.open(`${consoleUrl}/schemas/new`, TestIds.schemaAdministration.editor);
    await expect(async () => {
      await this.page.getByTestId(TestIds.schemaAdministration.displayName).fill(displayName);
      await this.page.getByTestId(TestIds.schemaAdministration.displayName).blur();
      await expect(this.page.getByTestId(TestIds.schemaAdministration.name)).toHaveValue(name, { timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
    await this.definition().getByLabel('Namespace').fill(namespace);
    const scope = this.page.getByTestId(TestIds.schemaAdministration.scopePicker);
    await scope.selectOption({ index: await this.workspaceOptionIndex(scope) });
    const scopeRef = await scope.inputValue();
    expect(scopeRef).toMatch(/^\/workspaces\//);

    await this.page.getByTestId(TestIds.schemaAdministration.addProperty).click();
    await this.renameSelectedProperty('profile');
    await this.page.getByTestId(TestIds.schemaAdministration.propertyType).selectOption('object');
    await this.page.getByTestId(TestIds.schemaAdministration.addChildProperty).click();
    await this.renameSelectedProperty('name');
    await this.selectedProperty().getByLabel('Required').check();
    await this.selectedProperty().getByLabel('Description').fill('Display name used by the contract.');
    await this.selectedProperty().getByLabel('Minimum length').fill('3');
    await this.propertyTreeItems().nth(0).click();
    await this.selectedProperty().getByLabel('Required').check();
    await this.page.getByTestId(TestIds.schemaAdministration.addProperty).click();
    await this.renameSelectedProperty('priority');
    await this.page.getByTestId(TestIds.schemaAdministration.propertyType).selectOption('integer');
    await this.selectedProperty().getByLabel('Minimum').fill('1');
    await this.selectedProperty().getByLabel('Maximum').fill('5');
    await this.page.getByTestId(TestIds.schemaAdministration.moveUp).click();
    await expect(this.propertyTreeItems().nth(0)).toContainText('priority');
    await expect(this.propertyTreeItems().nth(1)).toContainText('profile');
    await expect(this.propertyTreeItems().nth(2)).toContainText('name');
    await this.page.getByTestId(TestIds.schemaAdministration.save).click();
    await expect(this.page).toHaveURL(new RegExp(`/schemas/${name}\\?`));
    await expect(this.page.getByTestId(TestIds.schemaAdministration.overview)).toBeVisible();

    await this.openListAndFilter(consoleUrl, namespace, displayName);
    await this.page.getByTestId(TestIds.schemaAdministration.detailsLink).click();
    await expect(this.page.getByTestId(TestIds.schemaAdministration.overview)).toBeVisible();
    await this.page.getByTestId(TestIds.schemaAdministration.definitionTab).click();
    await this.definition().getByLabel('Description').fill('Reusable browser-tested contract.');
    await this.page.getByTestId(TestIds.schemaAdministration.save).click();
    await expect(this.page.locator('.form-alert-success[role="status"]').filter({ hasText: /saved/i })).toBeVisible();

    await this.exerciseSourceFallback();
    await this.exercisePreview();
    await this.exerciseStaleWrite(consoleUrl, scopeRef, namespace, name);
    await this.reloadEditor(consoleUrl, scopeRef, namespace, name, 'versions');
    await this.publishAndInspectVersion('1.0.0');
    await this.assertScopeBoundary(consoleUrl, namespace, name);
    return this.lastFixture = { name, namespace, scopeRef, version: '1.0.0' };
  }

  public async assertResponsive(): Promise<void> {
    const result = await this.page.evaluate(() => ({
      scrollWidth: document.documentElement.scrollWidth, viewportWidth: window.innerWidth,
      offenders: Array.from(document.querySelectorAll<HTMLElement>('body *'))
        .map(element => ({ testId: element.dataset.testid, right: Math.round(element.getBoundingClientRect().right) }))
        .filter(item => item.right > window.innerWidth + 1).slice(-10),
    }));
    expect(result.scrollWidth, `Overflowing elements: ${JSON.stringify(result.offenders)}`)
      .toBeLessThanOrEqual(result.viewportWidth);
  }

  private async openListAndFilter(consoleUrl: string, namespace: string, displayName: string): Promise<void> {
    await this.open(`${consoleUrl}/schemas`, TestIds.schemaAdministration.list);
    await this.page.getByTestId(TestIds.schemaAdministration.namespaceFilter).fill(namespace);
    const row = this.page.getByTestId(TestIds.schemaAdministration.row);
    await expect(row).toHaveCount(1);
    await expect(row).toContainText(displayName);
    const scopeRef = await this.page.getByTestId(TestIds.schemaAdministration.scopeFilter)
      .locator('option').filter({ hasText: '/workspaces/' }).first().getAttribute('value');
    if (!scopeRef) throw new Error('The Schema list did not expose its Workspace scope filter.');
    await this.page.getByTestId(TestIds.schemaAdministration.scopeFilter).selectOption(scopeRef);
    await expect(row).toHaveCount(1);
  }

  private async exercisePreview(): Promise<void> {
    await this.page.getByTestId(TestIds.schemaAdministration.designerTab).click();
    await this.page.getByTestId(TestIds.schemaAdministration.previewMode).click();
    const preview = this.page.getByTestId(TestIds.schemaAdministration.preview);
    await preview.getByRole('button', { name: 'Validate instance' }).click();
    await expect(preview.locator('.form-alert-danger[role="alert"]')).toContainText('$.profile');
    await preview.locator('[data-schema-path="$.profile.name"] input').fill('Ada');
    await preview.locator('[data-schema-path="$.priority"] input').fill('3');
    await preview.getByRole('button', { name: 'Validate instance' }).click();
    await expect(preview.locator('.form-alert-success[role="status"]')).toContainText(/valid/i);
  }

  private async exerciseSourceFallback(): Promise<void> {
    await this.page.getByTestId(TestIds.schemaAdministration.designerTab).click();
    await this.page.getByTestId(TestIds.schemaAdministration.sourceMode).click();
    await this.replaceMonaco(JSON.stringify({
      type: 'object', properties: { value: { oneOf: [{ type: 'string' }, { type: 'number' }] } },
    }, null, 2), 'oneOf');
    await this.page.getByTestId(TestIds.schemaAdministration.applySource).click();
    await expect(this.page.getByTestId(TestIds.schemaAdministration.sourceOnly)).toBeVisible();
    await expect(this.page.getByTestId(TestIds.schemaAdministration.designer)
      .locator('.form-alert-danger[role="alert"]')).toContainText('$.properties.value.oneOf');
    await this.page.getByTestId(TestIds.schemaAdministration.sourceMode).click();
    await this.expectMonacoContains('oneOf');

    const supported = {
      $schema: dialect, type: 'object',
      properties: {
        priority: { type: 'integer', minimum: 1, maximum: 5 },
        profile: { type: 'object', properties: {
          name: { type: 'string', description: 'Display name used by the contract.', minLength: 3 },
        }, required: ['name'], additionalProperties: false },
      },
      required: ['profile'], additionalProperties: false,
      'x-browser-contract': { preserves: ['extension', 'ordering'] },
    };
    await this.replaceMonaco(JSON.stringify(supported, null, 2), 'x-browser-contract');
    await this.page.getByTestId(TestIds.schemaAdministration.applySource).click();
    await expect(this.page.getByTestId(TestIds.schemaAdministration.designer)
      .locator('.form-alert-success[role="status"]')).toContainText(/valid/i);
    await this.page.getByTestId(TestIds.schemaAdministration.save).click();
    await expect(this.page.locator('.form-alert-success[role="status"]').filter({ hasText: /saved/i })).toBeVisible();
    await this.page.getByTestId(TestIds.schemaAdministration.sourceMode).click();
    await this.expectMonacoContains('x-browser-contract');
  }

  private async exerciseStaleWrite(consoleUrl: string, scopeRef: string, namespace: string, name: string): Promise<void> {
    await this.page.getByTestId(TestIds.schemaAdministration.definitionTab).click();
    const response = await this.page.request.get(this.schemaUrl(consoleUrl, scopeRef, namespace, name));
    await expectStatus(response, 200, 'read Schema before the competing update');
    const schema = await response.json() as SchemaWire;
    const etag = response.headers()['etag'];
    if (!etag) throw new Error('The Schema response did not expose an ETag.');
    schema.definition.description = 'Competing browser update.';
    const update = await this.page.request.put(this.schemaUrl(consoleUrl, scopeRef, namespace, name), {
      headers: { 'If-Match': etag }, data: { properties: schema.definition },
    });
    await expectStatus(update, 200, 'perform a competing Schema update');
    schema.definition.description = 'Stale browser update.';
    const stale = await this.page.request.put(this.schemaUrl(consoleUrl, scopeRef, namespace, name), {
      headers: { 'If-Match': etag }, data: { properties: schema.definition },
    });
    const staleBody = await stale.text();
    expect(stale.status(), staleBody).toBe(409);
    expect(staleBody).toContain('Resource version conflict');
  }

  private async publishAndInspectVersion(version: string): Promise<void> {
    await expect(async () => {
      await this.page.getByTestId(TestIds.schemaAdministration.publishVersion).fill(version);
      await this.page.getByTestId(TestIds.schemaAdministration.publish).click();
      await expect(this.page.getByTestId(TestIds.schemaAdministration.versionDetails))
        .toBeVisible({ timeout: 5_000 });
    }).toPass({ timeout: 20_000 });
    await expect(this.page.getByTestId(TestIds.schemaAdministration.versionLink)).toHaveText(version);
    await this.page.getByTestId(TestIds.schemaAdministration.versionLink).click();
    const immutable = this.page.getByTestId(TestIds.schemaAdministration.versionDetails);
    await immutable.getByTestId(TestIds.schemaAdministration.sourceMode).click();
    await this.expectMonacoContains('x-browser-contract');
    await expect(immutable.getByTestId(TestIds.schemaAdministration.applySource)).toHaveCount(0);
    await expect(immutable.getByTestId(TestIds.schemaAdministration.propertyName)).toBeDisabled();
  }

  private async assertScopeBoundary(consoleUrl: string, namespace: string, name: string): Promise<void> {
    const anonymous = await playwrightRequest.newContext();
    try {
      const unauthenticated = await anonymous.get(this.schemaUrl(consoleUrl,
        '/workspaces/00000000-0000-0000-0000-000000000001', namespace, name), { maxRedirects: 0 });
      expect([401, 302], 'An unauthenticated Schema request must not disclose resource state.')
        .toContain(unauthenticated.status());
    } finally {
      await anonymous.dispose();
    }
    const denied = await this.page.request.get(this.schemaUrl(consoleUrl,
      '/workspaces/00000000-0000-0000-0000-000000000001', namespace, name));
    const body = await denied.text();
    expect(denied.status(), body).toBe(422);
    expect(body).toContain('does not exist');
  }

  private async reloadEditor(consoleUrl: string, scopeRef: string, namespace: string, name: string, view: string): Promise<void> {
    await this.open(`${consoleUrl}/schemas/${encodeURIComponent(name)}?scopeRef=${encodeURIComponent(scopeRef)}`
      + `&namespace=${encodeURIComponent(namespace)}&view=${view}`, TestIds.schemaAdministration.editor);
  }

  private schemaUrl(consoleUrl: string, scopeRef: string, namespace: string, name: string): string {
    return `${consoleUrl}/api/schemas/${encodeURIComponent(name)}?scopeRef=${encodeURIComponent(scopeRef)}`
      + `&resourceNamespace=${encodeURIComponent(namespace)}`;
  }

  private definition(): Locator { return this.page.getByTestId(TestIds.schemaAdministration.definition); }
  private selectedProperty(): Locator { return this.page.getByTestId(TestIds.schemaAdministration.propertyRow); }
  private propertyTreeItems(): Locator { return this.page.getByTestId(TestIds.schemaAdministration.propertyTreeItem); }
  private monaco(): Locator { return this.page.getByTestId(TestIds.schemaAdministration.sourceEditor); }

  private async renameSelectedProperty(name: string): Promise<void> {
    await expect(async () => {
      const input = this.page.getByTestId(TestIds.schemaAdministration.propertyName);
      await input.fill(name);
      await input.press('Tab');
      await expect(input).toHaveValue(name, { timeout: 2_000 });
    }).toPass({ timeout: 10_000 });
  }

  private async replaceMonaco(value: string, marker: string): Promise<void> {
    await this.monaco().locator('.monaco-editor').waitFor({ state: 'visible' });
    await this.page.evaluate(next => {
      const api = (globalThis as typeof globalThis & {
        monaco?: { editor: { getModels(): Array<{ getValue(): string; setValue(value: string): void }> } };
      }).monaco;
      if (!api) throw new Error('Monaco is not available on the Schema source editor.');
      const model = api.editor.getModels().at(-1);
      if (!model) throw new Error('The Schema source editor has no Monaco model.');
      model.setValue(next);
    }, value);
    await expect.poll(() => this.monacoValue()).toContain(marker);
  }

  private async expectMonacoContains(marker: string): Promise<void> {
    await expect.poll(() => this.monacoValue()).toContain(marker);
  }

  private async monacoValue(): Promise<string> {
    return this.page.evaluate(() => {
      const api = (globalThis as typeof globalThis & {
        monaco?: { editor: { getModels(): Array<{ getValue(): string }> } };
      }).monaco;
      return api?.editor.getModels().at(-1)?.getValue() ?? '';
    });
  }

  private async workspaceOptionIndex(select: Locator): Promise<number> {
    const options = await select.locator('option').evaluateAll(elements => elements.map((element, index) => ({
      index, value: (element as HTMLOptionElement).value, disabled: (element as HTMLOptionElement).disabled,
    })));
    const target = options.find(option => option.value.startsWith('/workspaces/') && !option.disabled);
    if (!target) throw new Error('No writable Workspace scope is available for the Schema scenario.');
    return target.index;
  }

  private async open(url: string, marker: string): Promise<void> {
    const response = await this.page.goto(url, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Schema route '${url}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(marker).waitFor({ state: 'visible' });
  }
}

async function expectStatus(response: APIResponse, status: number, action: string): Promise<void> {
  expect(response.status(), `${action}: ${await response.text()}`).toBe(status);
}
