import { FlowCollectionPage } from './flow-collection.page.js';
import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit } from './controls.js';
import { CollectionVersionTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';

type DistributionMarker = keyof Pick<typeof TestIds.distribution,
  'extensions' | 'extensionDetails' | 'sourceProviders' | 'sourceProviderDetails' |
  'sourceRegistries' | 'sourceRegistryDiscovery' | 'sources' | 'bootstrapProfiles' |
  'packs' | 'packComposer' | 'packProjectDetails' | 'resourceScopes'>;

export interface PackProjectDefinition {
  publisher: string;
  name: string;
  version: string;
  displayName: string;
  resourceKind?: string;
  resourceName?: string;
}

export class DistributionPage {
  private referenceContext: string | null = null;
  public constructor(private readonly page: Page) {}

  public async open(consoleUrl: string, path: string, marker: DistributionMarker): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}${path}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Distribution route '${path}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.distribution[marker]).waitFor({ state: 'visible' });
  }

  public async inspectVersionPlacement(consoleUrl: string, route: string, locale: SupportedTestLocale): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}${route}`, { waitUntil: 'domcontentloaded' });
    expect(response?.ok()).toBe(true);
    const frame = this.page.locator('.resource-collection').first();
    await expect(frame).toBeVisible();
    if (route === '/flows') {
      const flows = new FlowCollectionPage(this.page);
      await flows.assertCompactStructure(locale);
      await flows.table();
      await flows.assertCompactStructure(locale);
      await flows.cards();
      await flows.assertCompactStructure(locale);
      return;
    }
    const labels = CollectionVersionTextByLocale[locale];
    const expected = route.startsWith('/data-source-profiles') ? [labels.active]
      : route.startsWith('/agents') ? [labels.generation] : [labels.version];
    const assertIdentity = async () => {
      const identity = frame.locator('.collection-identity').first();
      await expect(identity.locator('.collection-version-label')).toHaveText(expected);
      await expect.poll(async () => {
        const text = await identity.locator('.collection-identity-text').boundingBox();
        const version = await identity.locator('.collection-identity-versions').boundingBox();
        return text && version ? Math.abs(text.x - version.x) : Infinity;
      }, { message: 'Versions align with identity text rather than the family icon after interactive rendering' }).toBeLessThan(1);
      await expect(identity.locator('.collection-qualified-name code')).toBeVisible();
      expect(await identity.locator('.collection-qualified-name code').textContent()).not.toMatch(/\s\/|\/\s/);
      expect(await this.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
    };
    await assertIdentity();
    await frame.locator('.collection-view-switch button').first().click();
    for (const label of expected) await expect(frame.getByRole('columnheader', { name: label, exact: true })).toHaveCount(0);
    await assertIdentity();
    await frame.locator('.collection-view-switch button').nth(1).click();
    await assertIdentity();
  }

  public async showCollectionTable(): Promise<void> {
    const frame = this.page.locator('.resource-collection').first();
    await frame.locator('.collection-view-switch button').first().click();
    await expect(frame.locator('table')).toBeVisible();
  }

  public async inspectDirectEnrollmentActions(consoleUrl: string): Promise<void> {
    await this.open(consoleUrl, '/extensions?tab=enrollments&context=%2Finstance', 'extensions');
    await this.page.locator(`[data-testid="${TestIds.distribution.extensions}"][data-interactive="true"]`).waitFor({ state: 'visible' });
    await this.page.getByTestId(TestIds.distribution.extensionEnrollmentsTab).click();
    const primary = this.page.locator('.enrollment-actions .collection-primary-actions button').first();
    await expect(primary).toBeVisible();
    await expect(primary.locator('xpath=ancestor::details')).toHaveCount(0);
    await primary.focus(); await expect(primary).toBeFocused();
    const menu = this.page.locator('.enrollment-actions .collection-actions-menu').first();
    if (await menu.count()) {
      const trigger = menu.locator('summary');
      await trigger.focus(); await trigger.press('ArrowDown');
      await expect(menu.locator('.collection-actions-panel button').first()).toBeFocused();
      await this.page.keyboard.press('Escape');
      await expect(trigger).toBeFocused();
      await expect(menu).not.toHaveAttribute('open', '');
    }
    const identity = this.page.locator('.enrollment-identity .collection-identity').first();
    await expect(identity.locator('.collection-version')).toBeVisible();
    const viewport = await this.page.evaluate(() => ({
      width: innerWidth,
      scrollWidth: document.documentElement.scrollWidth,
      overflow: [...document.querySelectorAll('body *')].map(element => ({ element, rect: element.getBoundingClientRect() }))
        .filter(({ element, rect }) => rect.right > innerWidth + 1 && getComputedStyle(element).display !== 'none')
        .map(({ element, rect }) => ({ tag: element.tagName, classes: element.className.toString(), right: rect.right, text: element.textContent?.trim().slice(0, 80) })).slice(0, 12),
    }));
    expect(viewport.scrollWidth, JSON.stringify(viewport)).toBeLessThanOrEqual(viewport.width + 1);
  }

  public async inspectDirectProfileChoice(consoleUrl: string): Promise<void> {
    await this.page.goto(`${consoleUrl}/agents/new?context=${encodeURIComponent(this.referenceContext ?? '/instance')}`, { waitUntil: 'domcontentloaded' });
    await expect(this.page.getByTestId(TestIds.agentEditor.form)).toHaveAttribute('data-interactive', 'true');
    const toggle = this.page.getByTestId(TestIds.agentEditor.modelProfileToggle);
    await expect(toggle).toBeVisible();
    if (await toggle.getAttribute('aria-expanded') !== 'true') await toggle.click();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const choice = this.page.getByTestId(TestIds.agentEditor.modelProfileChoice).first();
    await expect(choice).toBeVisible();
    await expect(choice.locator('xpath=ancestor::details')).toHaveCount(0);
    await choice.scrollIntoViewIfNeeded();
    expect(await this.page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1)).toBe(true);
  }

  public async openFirstExtensionDetails(consoleUrl: string): Promise<void> {
    await this.open(consoleUrl, '/extensions', 'extensions');
    await this.page.locator(`[data-testid="${TestIds.distribution.extensions}"][data-interactive="true"]`).waitFor({ state: 'visible' });
    this.referenceContext = await this.page.getByTestId(TestIds.console.shell).getAttribute('data-context-scope');
    await this.page.getByTestId(TestIds.distribution.extensionCatalogTab).click();
    const detail = this.page.locator('#extensions-panel-catalog a.resource-name-link[href^="/extensions/"]').first();
    await detail.waitFor({ state: 'visible' });
    await detail.click();
    await this.page.getByTestId(TestIds.distribution.extensionDetails).waitFor({ state: 'visible' });
  }

  public async createSourceProvider(consoleUrl: string, name: string, displayName: string): Promise<void> {
    await this.open(consoleUrl, '/sourceproviders/new', 'sourceProviderDetails');
    await this.page.locator(`[data-testid="${TestIds.distribution.sourceProviderDetails}"][data-interactive="true"]`).waitFor({ state: 'visible' });
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.providerDisplayName), displayName);
    const extension = this.page.getByTestId(TestIds.distribution.providerExtension);
    await extension.selectOption({ index: 1 });
    const contribution = this.page.getByTestId(TestIds.distribution.providerContribution);
    await expect(contribution.locator('option')).toHaveCount(2);
    await contribution.selectOption({ index: 1 });
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.providerName), name);
    await Promise.all([
      this.page.waitForURL(url => url.pathname === `/sourceproviders/${encodeURIComponent(name)}`),
      this.page.getByTestId(TestIds.distribution.providerSubmit).click(),
    ]);
    await this.page.getByTestId(TestIds.distribution.sourceProviderDetails).waitFor({ state: 'visible' });
  }

  public async importSource(consoleUrl: string, publisher: string, name: string, yaml: string): Promise<void> {
    await this.open(consoleUrl, '/settings/sources', 'sources');
    await this.page.locator(`[data-testid="${TestIds.distribution.sources}"][data-interactive="true"]`).waitFor({ state: 'visible' });
    await this.page.getByTestId(TestIds.distribution.sourceImportOpen).click();
    await this.page.getByTestId(TestIds.distribution.sourceImportYamlMode).click();
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.sourceImportYaml), yaml);
    await Promise.all([
      this.page.waitForURL(url => url.pathname === `/settings/sources/${encodeURIComponent(publisher)}/${encodeURIComponent(name)}`),
      this.page.getByTestId(TestIds.distribution.sourceImportSubmit).click(),
    ]);
    await this.sourceDetails(publisher, name).waitFor({ state: 'visible' });
  }

  public async createPackProject(consoleUrl: string, project: PackProjectDefinition): Promise<string> {
    await this.open(consoleUrl, '/pack-projects/new', 'packComposer');
    await this.page.locator(`[data-testid="${TestIds.distribution.packComposer}"][data-interactive="true"]`).waitFor({ state: 'visible' });
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.packProjectPublisher), project.publisher);
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.packProjectName), project.name);
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.packProjectVersion), project.version);
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.packProjectDisplayName), project.displayName);
    await expect(this.page.getByTestId(TestIds.distribution.packProjectName)).toHaveValue(project.name);
    const add = this.page.locator(`[data-resource-kind="${project.resourceKind ?? 'Flow'}"][data-resource-name="${project.resourceName ?? 'system-direct-agent-dotnet-expert'}"]`)
      .getByTestId(TestIds.distribution.packProjectAddResource);
    await add.waitFor({ state: 'visible' });
    await add.click();
    const create = this.page.getByTestId(TestIds.distribution.packProjectCreate);
    await expect(create).toBeEnabled();
    await Promise.all([
      this.page.waitForURL(url => /^\/pack-projects\/[0-9a-f-]{36}$/i.test(url.pathname)),
      create.click(),
    ]);
    await this.page.getByTestId(TestIds.distribution.packProjectDetails).waitFor({ state: 'visible' });
    return this.page.url().split('/').at(-1)!;
  }

  public async openSourceDetailsFromList(publisher: string, name: string): Promise<void> {
    const row = this.page.locator(
      `[data-testid="${TestIds.distribution.sourceRow}"][data-source-publisher="${attributeValue(publisher)}"][data-source-name="${attributeValue(name)}"]`,
    );
    await row.locator('a.resource-name-link').click();
    await this.sourceDetails(publisher, name).waitFor({ state: 'visible' });
  }

  public async buildPackProject(): Promise<void> {
    await this.page.getByTestId(TestIds.distribution.packProjectBuild).click();
    await this.page.locator(`[data-testid="${TestIds.distribution.packProjectStatus}"][data-state="success"]`).waitFor({ state: 'visible' });
  }

  public async installPackProject(): Promise<string> {
    await this.page.getByTestId(TestIds.distribution.packProjectInstall).click();
    await this.page.getByTestId(TestIds.distribution.packProjectStatus).waitFor({ state: 'visible' });
    return this.page.getByTestId(TestIds.distribution.packProjectNamespace).innerText();
  }

  public sourceDetails(publisher: string, name: string): Locator {
    return this.page.locator(`[data-testid="${TestIds.distribution.sourceDetails}"][data-source-publisher="${attributeValue(publisher)}"][data-source-name="${attributeValue(name)}"]`);
  }
}

function attributeValue(value: string): string {
  if (!/^[a-zA-Z0-9_.-]+$/.test(value)) throw new Error(`Unsupported distribution identifier '${value}'.`);
  return value;
}
