import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit, selectFirstAvailable } from './controls.js';

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
  public constructor(private readonly page: Page) {}

  public async applyProvenanceBootstrapProfile(consoleUrl: string): Promise<void> {
    const identityResponse = await this.page.request.get(new URL('/api/identity/context', consoleUrl).toString());
    expect(identityResponse.ok()).toBeTruthy();
    const { context } = await identityResponse.json() as { context: { tenantId: string; workspaceId: string } };
    const target = { tenantId: context.tenantId, workspaceId: context.workspaceId };
    const profile = 'playwright-bootstrap-provenance';
    const profiles = [profile];
    const bindings = [];
    for (const [name, targetKind] of [['assistant-model', 'modelProfile'], ['assistant-runtime', 'runtimeProfile']] as const) {
      const response = await this.page.request.post(new URL('/api/bootstrap/binding-targets', consoleUrl).toString(), { data: { target, targetKind, profiles } });
      expect(response.ok()).toBeTruthy();
      const options = await response.json() as Array<{ name: string; namespace: string; scopeRef: string | null; planned: boolean }>;
      const selected = options.filter(option => !option.planned).sort((a, b) => a.name.localeCompare(b.name))[0];
      if (!selected) throw new Error('The provenance bootstrap fixture requires an existing ' + targetKind + '.');
      bindings.push({ profile, name, target: { name: selected.name, namespace: selected.namespace, scopeRef: selected.scopeRef } });
    }
    const selection = { profiles, target, bindings };
    const previewResponse = await this.page.request.post(new URL('/api/bootstrap/profiles/preview', consoleUrl).toString(), { data: selection });
    expect(previewResponse.ok()).toBeTruthy();
    const preview = await previewResponse.json() as { digest: string; canApply: boolean };
    expect(preview.canApply).toBe(true);
    const applied = await this.page.request.post(new URL('/api/bootstrap/applications', consoleUrl).toString(), { data: { ...selection, expectedDigest: preview.digest } });
    expect(applied.status()).toBe(201);
    const receipt = await applied.json() as { definition: { error: string | null } };
    expect(receipt.definition.error).toBeNull();
    const entry = await this.page.request.get(new URL('/api/namespaces/playwright.bootstrap-provenance-source/management/entries/ask-bootstrap-provenance', consoleUrl).toString());
    expect(entry.status()).toBe(200);
  }


  public async open(consoleUrl: string, path: string, marker: DistributionMarker): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}${path}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Distribution route '${path}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.distribution[marker]).waitFor({ state: 'visible' });
  }

  public async openFirstExtensionDetails(consoleUrl: string): Promise<void> {
    await this.open(consoleUrl, '/extensions', 'extensions');
    await this.page.locator(`[data-testid="${TestIds.distribution.extensions}"][data-interactive="true"]`).waitFor({ state: 'visible' });
    await this.page.getByTestId(TestIds.distribution.extensionCatalogTab).click();
    const detail = this.page.locator('#extensions-panel-catalog a.text-button[href^="/extensions/"]').first();
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
    for (const binding of await this.page.locator('.studio-bindings select').all()) {
      if (await binding.inputValue()) continue;
      const options = await binding.locator('option').evaluateAll(elements => elements
        .filter(element => !(element as HTMLOptionElement).disabled && (element as HTMLOptionElement).value)
        .map(element => (element as HTMLOptionElement).value));
      await selectFirstAvailable(binding, options, 'Pack resource binding');
    }
    const status = this.page.getByTestId(TestIds.distribution.packProjectStatus);
    const previous = await status.innerText();
    const install = this.page.getByTestId(TestIds.distribution.packProjectInstall);
    await expect(install).toBeEnabled();
    await install.click();
    await expect(status).toHaveAttribute('data-state', 'success');
    await expect(status).not.toHaveText(previous);
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
