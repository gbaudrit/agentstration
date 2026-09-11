import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit } from './controls.js';

type DistributionMarker = keyof Pick<typeof TestIds.distribution,
  'extensions' | 'extensionDetails' | 'sourceProviders' | 'sourceProviderDetails' |
  'sourceRegistries' | 'sourceRegistryDiscovery' | 'sources' | 'bootstrapProfiles' |
  'packs' | 'packComposer' | 'packProjectDetails' | 'resourceScopes'>;

export interface PackProjectDefinition {
  publisher: string;
  name: string;
  version: string;
  displayName: string;
}

export class DistributionPage {
  public constructor(private readonly page: Page) {}

  public async open(consoleUrl: string, path: string, marker: DistributionMarker): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}${path}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Distribution route '${path}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.distribution[marker]).waitFor({ state: 'visible' });
  }

  public async openFirstExtensionDetails(consoleUrl: string): Promise<void> {
    await this.open(consoleUrl, '/extensions', 'extensions');
    await this.page.getByTestId(TestIds.distribution.extensionCatalogTab).click();
    const detail = this.page.locator('#extensions-panel-catalog a.text-button[href^="/extensions/"]').first();
    await detail.waitFor({ state: 'visible' });
    await detail.click();
    await this.page.getByTestId(TestIds.distribution.extensionDetails).waitFor({ state: 'visible' });
  }

  public async createSourceProvider(consoleUrl: string, name: string, displayName: string): Promise<void> {
    await this.open(consoleUrl, '/sourceproviders/new', 'sourceProviderDetails');
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.providerName), name);
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.providerDisplayName), displayName);
    const extension = this.page.getByTestId(TestIds.distribution.providerExtension);
    await extension.selectOption({ index: 1 });
    const contribution = this.page.getByTestId(TestIds.distribution.providerContribution);
    await expect(contribution.locator('option')).toHaveCount(2);
    await contribution.selectOption({ index: 1 });
    await Promise.all([
      this.page.waitForURL(url => url.pathname === `/sourceproviders/${encodeURIComponent(name)}`),
      this.page.getByTestId(TestIds.distribution.providerSubmit).click(),
    ]);
    await this.page.getByTestId(TestIds.distribution.sourceProviderDetails).waitFor({ state: 'visible' });
  }

  public async importSource(consoleUrl: string, publisher: string, name: string, yaml: string): Promise<void> {
    await this.open(consoleUrl, '/settings/sources', 'sources');
    await this.page.getByTestId(TestIds.distribution.sourceImportOpen).click();
    await this.page.getByTestId(TestIds.distribution.sourceImportYamlMode).click();
    await this.page.getByTestId(TestIds.distribution.sourceImportYaml).fill(yaml);
    await Promise.all([
      this.page.waitForURL(url => url.pathname === `/settings/sources/${encodeURIComponent(publisher)}/${encodeURIComponent(name)}`),
      this.page.getByTestId(TestIds.distribution.sourceImportSubmit).click(),
    ]);
    await this.sourceDetails(publisher, name).waitFor({ state: 'visible' });
  }

  public async createPackProject(consoleUrl: string, project: PackProjectDefinition): Promise<string> {
    await this.open(consoleUrl, '/pack-projects/new', 'packComposer');
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.packProjectPublisher), project.publisher);
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.packProjectName), project.name);
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.packProjectVersion), project.version);
    await fillAndCommit(this.page.getByTestId(TestIds.distribution.packProjectDisplayName), project.displayName);
    const add = this.page.getByTestId(TestIds.distribution.packProjectAddResource).first();
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

  public async buildPackProject(): Promise<void> {
    await this.page.getByTestId(TestIds.distribution.packProjectBuild).click();
    await this.page.locator(`[data-testid="${TestIds.distribution.packProjectStatus}"][data-state="success"]`).waitFor({ state: 'visible' });
  }

  public sourceDetails(publisher: string, name: string): Locator {
    return this.page.locator(`[data-testid="${TestIds.distribution.sourceDetails}"][data-source-publisher="${attributeValue(publisher)}"][data-source-name="${attributeValue(name)}"]`);
  }
}

function attributeValue(value: string): string {
  if (!/^[a-zA-Z0-9_.-]+$/.test(value)) throw new Error(`Unsupported distribution identifier '${value}'.`);
  return value;
}
