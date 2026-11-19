import { createHash, randomUUID } from 'node:crypto';
import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { ConnectedStorageCollectionTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';
import { CollectionContractPage } from './collection-contract.page.js';
import { selectResourceSection } from './resource-sections.js';

interface Descriptor { version: string; schemaDigest: string; contractDigest: string }
interface TypeResource { metadata: { name: string; namespace: string }; scopeRef: string; uid: string; definition: { versions: Descriptor[] } }
export interface ConnectedStorageFixture { name: string; namespaces: string[]; service: string; type: string; title: string }

/** Real offline metadata resources; no remote Storage/Connected Service operation is invoked. */
export class ConnectedStorageCollectionPage {
  public constructor(private readonly page: Page) {}

  public async prepare(consoleUrl: string): Promise<ConnectedStorageFixture> {
    const suffix = randomUUID().slice(0, 8);
    const name = `collection-storage-${suffix}`;
    const namespaces = [`collection-${suffix}-long-qualified-identity`, `collection-${suffix}-other`];
    const title = 'Shared storage — ' + 'long readable resource identity '.repeat(5);
    const schema = { type: 'object' };
    const digest = 'sha256:' + createHash('sha256').update(JSON.stringify(schema)).digest('hex');
    const headers = { 'X-Agentstration-Context': '/instance' };
    const typeName = `collection-type-${suffix}`;
    const typeResponse = await this.page.request.post(`${consoleUrl}/api/storage-types`, { headers, data: {
      name: typeName, namespace: namespaces[0], scopeRef: '/instance', properties: {
        displayName: `Offline collection type ${suffix}`, preferredVersion: '1.0.0', versions: [{
          version: '1.0.0', schemaDigest: digest, configurationSchema: schema,
          envelopeSchemaDigest: digest, envelopeSchema: schema,
        }],
      },
    } });
    expect(typeResponse.status(), await typeResponse.text()).toBe(201);
    const type = await typeResponse.json() as TypeResource;
    const version = type.definition.versions[0];
    for (const namespace of namespaces) {
      const response = await this.page.request.post(`${consoleUrl}/api/storages`, { headers, data: {
        name, namespace, scopeRef: '/instance', properties: {
          displayName: title, type: { type: { name: type.metadata.name, namespace: type.metadata.namespace, scopeRef: type.scopeRef },
            typeUid: type.uid, version: version.version, schemaDigest: version.schemaDigest, contractDigest: version.contractDigest },
          configuration: {}, envelope: {},
        },
      } });
      expect(response.status(), await response.text()).toBe(201);
    }
    const serviceTypeResponse = await this.page.request.post(`${consoleUrl}/api/connected-service-types`, { headers, data: {
      name: `collection-service-type-${suffix}`, scopeRef: '/instance', properties: {
        displayName: 'Offline collection service type', preferredVersion: '1.0.0', versions: [{
          version: '1.0.0', schemaDigest: digest, schema, inputs: [], exports: [],
        }],
      },
    } });
    expect(serviceTypeResponse.status(), await serviceTypeResponse.text()).toBe(201);
    const serviceType = await serviceTypeResponse.json() as TypeResource;
    const service = `collection-service-${suffix}`;
    const serviceResponse = await this.page.request.post(`${consoleUrl}/api/connected-services`, { headers, data: {
      name: service, scopeRef: '/instance', properties: {
        displayName: 'Offline connected service — ' + 'long readable identity '.repeat(6),
        type: { type: { name: serviceType.metadata.name, namespace: serviceType.metadata.namespace, scopeRef: serviceType.scopeRef },
          typeUid: serviceType.uid, version: '1.0.0', schemaDigest: digest }, options: {}, bindings: [],
      },
    } });
    expect(serviceResponse.status(), await serviceResponse.text()).toBe(201);
    const missing = await this.page.request.get(`${consoleUrl}/api/storages/${name}?scopeRef=%2Finstance&namespace=absent-${suffix}`, { headers });
    expect(missing.status()).toBe(404);
    return { name, namespaces, service, type: typeName, title };
  }

  public async inspectStorages(consoleUrl: string, fixture: ConnectedStorageFixture, locale: SupportedTestLocale, touch: boolean): Promise<void> {
    await this.open(consoleUrl, '/storages');
    const rows = this.page.getByTestId(TestIds.connectedStorageCollections.storageRow);
    for (const namespace of fixture.namespaces) {
      const row = rows.filter({ hasText: `${namespace}/${fixture.name}` });
      await expect(row).toHaveCount(1);
      await this.identity(row, `${namespace}/${fixture.name}`, fixture.name, namespace, touch);
    }
    await new CollectionContractPage(this.page).inspect(locale);
    await this.inspectCatalog(consoleUrl, fixture);
    const row = rows.filter({ hasText: `${fixture.namespaces[0]}/${fixture.name}` });
    await row.getByTestId(TestIds.connectedStorageCollections.storageName).click();
    await expect(this.page.getByTestId(TestIds.connectedStorageCollections.storageDetails)).toBeVisible();
    expect(new URL(this.page.url()).searchParams.get('namespace')).toBe(fixture.namespaces[0]);
    await this.cancelConfirmation(this.page.getByTestId(TestIds.connectedStorageCollections.storageDeactivate), locale);
    await this.page.getByTestId(TestIds.connectedStorageCollections.storageDefinition).click();
    await this.clearAndRestore(this.page.getByTestId(TestIds.connectedStorageCollections.storageDisplayName), fixture.title);
    await this.assertNoOverflow();
  }

  public async inspectTypes(consoleUrl: string, fixture: ConnectedStorageFixture, locale: SupportedTestLocale): Promise<void> {
    await this.open(consoleUrl, '/storage-types');
    const row = this.page.getByTestId(TestIds.connectedStorageCollections.storageTypeRow).filter({ hasText: fixture.type });
    await expect(row).toHaveCount(1);
    await expect(row.locator('.collection-version code')).toHaveText('1.0.0');
    await new CollectionContractPage(this.page).inspect(locale);
    await row.getByTestId(TestIds.connectedStorageCollections.storageTypeName).click();
    await expect(this.page).toHaveURL(url => url.pathname === `/storage-types/${fixture.type}`);
    expect(new URL(this.page.url()).pathname).toBe(`/storage-types/${fixture.type}`);
    expect(new URL(this.page.url()).searchParams.get('namespace')).toBe(fixture.namespaces[0]);
    for (const namespace of fixture.namespaces) {
      const usage = this.page.locator('.resource-links > li').filter({ hasText: `${namespace}/${fixture.name}` });
      await expect(usage).toHaveCount(1);
      await this.identity(usage, `${namespace}/${fixture.name}`, fixture.name, namespace, false);
    }
    await this.assertNoOverflow();
  }

  public async inspectServices(consoleUrl: string, fixture: ConnectedStorageFixture, locale: SupportedTestLocale, touch: boolean): Promise<void> {
    await this.open(consoleUrl, '/connected-services');
    const row = this.page.getByTestId(TestIds.connectedStorageCollections.serviceRow).filter({ hasText: fixture.service });
    await expect(row).toHaveCount(1);
    await this.identity(row, `default/${fixture.service}`, fixture.service, 'default', touch);
    await new CollectionContractPage(this.page).inspect(locale);
    await row.getByTestId(TestIds.connectedStorageCollections.serviceName).click();
    const header = this.page.getByTestId(TestIds.connectedStorageCollections.serviceDetails);
    await expect(header).toBeVisible();
    const activation = header.getByRole('switch');
    await expect(activation).toBeChecked();
    await this.cancelConfirmation(activation, locale);
    await expect(activation).toBeChecked();
    for (const section of ['overview', 'discovery', 'access', 'yaml']) {
      await selectResourceSection(this.page, section);
      await expect(activation).toBeVisible();
      await expect(activation).toBeChecked();
      await this.assertNoOverflow();
    }
    await expect(this.page.getByTestId(TestIds.connectedStorageCollections.serviceYamlViewer).locator('textarea[readonly]')).toBeVisible();
    await selectResourceSection(this.page, 'definition/configuration');
    const field = this.page.getByTestId(TestIds.connectedStorageCollections.serviceDisplayName);
    await expect(field).toBeVisible();
    await this.clearAndRestore(field, await field.inputValue());
    await this.assertNoOverflow();
  }

  public async captureList(consoleUrl: string, surface: 'storages' | 'types' | 'services'): Promise<Buffer> {
    const route = surface === 'types' ? '/storage-types' : surface === 'services' ? '/connected-services' : '/storages';
    await this.open(consoleUrl, route);
    await expect(this.page.locator('.resource-collection').first()).toBeVisible();
    return this.captureCurrent();
  }

  public async layoutDiagnostics(): Promise<unknown[]> {
    return this.page.locator('main *').evaluateAll(elements => elements
      .filter(element => { const bounds = element.getBoundingClientRect(); return bounds.width > 0 && bounds.right > innerWidth + 1; })
      .slice(0, 24).map(element => {
        const bounds = element.getBoundingClientRect();
        const style = getComputedStyle(element);
        return { tag: element.tagName, classes: element.className, width: bounds.width, right: bounds.right,
          minWidth: style.minWidth, maxWidth: style.maxWidth, columns: style.gridTemplateColumns,
          display: style.display, overflowX: style.overflowX, whiteSpace: style.whiteSpace };
      }));
  }

  public async captureCurrent(): Promise<Buffer> {
    await this.page.evaluate(() => {
      window.scrollTo(0, 0);
      return new Promise<void>(resolve => requestAnimationFrame(() => resolve()));
    });
    return this.page.screenshot({ fullPage: true });
  }

  private async inspectCatalog(consoleUrl: string, fixture: ConnectedStorageFixture): Promise<void> {
    const qualified = `${fixture.namespaces[0]}/${fixture.type}`;
    const evidence = this.page.locator('details').filter({
      has: this.page.locator('.collection-qualified-name').filter({ hasText: qualified }),
    });
    await expect(evidence).toHaveCount(1);
    await expect(evidence).toHaveJSProperty('open', false);
    const summary = evidence.locator('summary');
    const copy = summary.locator('.copy-value');
    const before = this.page.url();
    await copy.focus();
    await copy.click();
    await expect.poll(() => this.page.evaluate(() => navigator.clipboard.readText())).toBe(qualified);
    expect(this.page.url()).toBe(before);
    await expect(evidence).toHaveJSProperty('open', false);
    await summary.focus();
    await summary.press('Enter');
    await expect(evidence).toHaveJSProperty('open', true);
    await summary.press('Enter');
    await expect(evidence).toHaveJSProperty('open', false);
    await summary.locator('.resource-name-link').click();
    await expect(this.page).toHaveURL(url => url.pathname === `/storage-types/${fixture.type}`);
    expect(new URL(this.page.url()).pathname).toBe(`/storage-types/${fixture.type}`);
    expect(new URL(this.page.url()).searchParams.get('namespace')).toBe(fixture.namespaces[0]);
    expect(new URL(this.page.url()).searchParams.get('scopeRef')).toBe('/instance');
    await this.open(consoleUrl, '/storages');
  }

  private async identity(row: Locator, qualified: string, name: string, namespace: string, touch: boolean): Promise<void> {
    const identity = row.locator('.collection-identity');
    const link = identity.locator('.resource-name-link');
    await expect(link).toBeVisible();
    const url = new URL((await link.getAttribute('href'))!, this.page.url());
    expect(decodeURIComponent(url.pathname)).toContain(name);
    expect(url.searchParams.get('scopeRef')).toBe('/instance');
    expect(url.searchParams.get('namespace')).toBe(namespace);
    await expect(identity.locator('.scope-letter')).toHaveText('I');
    await expect(identity.locator('.managed-resource-badge')).toHaveCount(0);
    const scope = identity.locator('.resource-owner-scope button');
    await scope.focus();
    await expect(identity.locator('.resource-owner-scope [role=tooltip]')).toBeVisible();
    await this.page.keyboard.press('Escape');
    await expect(scope).toBeFocused();
    if (touch) { await scope.tap(); await expect(identity.locator('.resource-owner-scope [role=tooltip]')).toBeVisible(); await this.page.keyboard.press('Escape'); }
    const copy = identity.locator('.copy-value');
    await copy.focus();
    const before = this.page.url();
    await copy.click();
    await expect.poll(() => this.page.evaluate(() => navigator.clipboard.readText())).toBe(qualified);
    expect(this.page.url()).toBe(before);
  }

  private async clearAndRestore(field: Locator, value: string): Promise<void> {
    await expect(field).toHaveValue(value);
    const wrapper = field.locator('xpath=ancestor::*[contains(@class,"form-field")][1]');
    await wrapper.locator('.field-clear').click();
    await expect(field).toHaveValue('');
    await expect(field).toBeFocused();
    await field.fill(value);
    await field.blur();
    await expect(field).toHaveValue(value);
  }

  private async cancelConfirmation(trigger: Locator, locale: SupportedTestLocale): Promise<void> {
    await trigger.click();
    const dialog = this.page.getByRole('alertdialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.getByRole('button', { name: ConnectedStorageCollectionTextByLocale[locale].cancel, exact: true })).toBeVisible();
    await this.page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(trigger).toBeFocused();
  }

  private async open(consoleUrl: string, route: string): Promise<void> {
    expect((await this.page.goto(`${consoleUrl}${route}?context=%2Finstance`))?.ok()).toBeTruthy();
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-preferences-ready', 'true');
    await expect(this.page.locator('main .state-panel .spinner')).toHaveCount(0);
  }

  private async assertNoOverflow(): Promise<void> {
    expect(await this.page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(0);
  }
}
