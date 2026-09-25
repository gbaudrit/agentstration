import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit } from './controls.js';

export interface ModelAdministrationDefinition {
  providerName: string;
  providerDisplayName: string;
  profileName: string;
  profileDisplayName: string;
  runtimeName: string;
  runtimeDisplayName: string;
}

export class ModelAdministrationPage {
  public constructor(private readonly page: Page) {}

  public async createProvider(consoleUrl: string, definition: ModelAdministrationDefinition): Promise<Locator> {
    await this.open(consoleUrl, '/modelproviders/new', TestIds.resourceAdministration.modelProviderEditor);
    await this.waitForInteractive(TestIds.modelAdministration.providerForm);
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.providerName), definition.providerName);
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.providerDisplayName), definition.providerDisplayName);
    await selectMatchingOption(this.page.getByTestId(TestIds.modelAdministration.providerExtension), 'ollama-extension');
    await selectFirstOption(this.page.getByTestId(TestIds.modelAdministration.providerContribution));
    await expect(this.page.getByTestId(TestIds.modelAdministration.providerName)).toHaveValue(definition.providerName);
    await this.page.getByTestId(TestIds.modelAdministration.providerSave).click();
    await this.page.waitForURL(url => url.pathname === `/modelproviders/${definition.providerName}`);
    const marker = this.page.getByTestId(TestIds.resourceAdministration.modelProviderEditor);
    await marker.waitFor({ state: 'visible' });
    await this.openProviderConfiguration();
    const status = this.page.getByTestId(TestIds.modelAdministration.providerStatus);
    const previousCheck = await status.getAttribute('data-checked-at');
    await this.page.getByTestId(TestIds.modelAdministration.providerTest).click();
    await expect.poll(() => status.getAttribute('data-checked-at')).not.toBe(previousCheck);
    await expect(status).toBeVisible();
    return marker;
  }

  public async createRuntime(consoleUrl: string, definition: ModelAdministrationDefinition): Promise<Locator> {
    await this.open(consoleUrl, '/runtimeprofiles/new', TestIds.resourceAdministration.runtimeProfileEditor);
    await this.waitForInteractive(TestIds.modelAdministration.runtimeForm);
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.runtimeName), definition.runtimeName);
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.runtimeDisplayName), definition.runtimeDisplayName);
    await this.page.getByTestId(TestIds.modelAdministration.runtimeSave).click();
    await this.page.waitForURL(url => url.pathname === `/runtimeprofiles/${definition.runtimeName}`);
    await this.waitForPersisted(TestIds.modelAdministration.runtimeForm);
    return this.page.getByTestId(TestIds.resourceAdministration.runtimeProfileEditor);
  }

  public async refreshProviderModels(consoleUrl: string, providerName: string): Promise<void> {
    const response = await this.page.request.post(
      `${consoleUrl}/api/modelproviders/${encodeURIComponent(providerName)}/models/refresh`,
    );
    if (!response.ok()) {
      throw new Error(`Model discovery refresh returned HTTP ${response.status()}.`);
    }
  }

  public async overrideFirstModelContextLimit(consoleUrl: string, providerName: string, contextTokens: number): Promise<void> {
    await this.open(consoleUrl, `/modelproviders/${encodeURIComponent(providerName)}`, TestIds.resourceAdministration.modelProviderEditor);
    await this.page.getByTestId(TestIds.modelAdministration.openModelDetails).first().click();
    await this.page.getByTestId(TestIds.resourceAdministration.modelDetails).waitFor({ state: 'visible' });
    await this.page.getByTestId(TestIds.modelAdministration.editModelOverride).click();
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.modelOverrideContextTokens), contextTokens.toString());
    await this.page.getByTestId(TestIds.modelAdministration.saveModelOverride).click();
    await this.page.getByTestId(TestIds.modelAdministration.modelOverrideMessage).waitFor({ state: 'visible' });
  }

  public async createProfile(consoleUrl: string, definition: ModelAdministrationDefinition): Promise<Locator> {
    await this.open(consoleUrl, '/modelprofiles/new', TestIds.resourceAdministration.modelProfileEditor);
    await this.waitForInteractive(TestIds.modelAdministration.profileForm);
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.profileName), definition.profileName);
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.profileDisplayName), definition.profileDisplayName);
    await selectMatchingOption(this.page.getByTestId(TestIds.modelAdministration.profileProvider), definition.providerName);
    await selectFirstOption(this.page.getByTestId(TestIds.modelAdministration.profileModel));
    await this.page.getByTestId(TestIds.modelAdministration.profileSave).click();
    await this.page.waitForURL(url => url.pathname === `/modelprofiles/${definition.profileName}`);
    await this.waitForPersisted(TestIds.modelAdministration.profileForm);
    return this.page.getByTestId(TestIds.resourceAdministration.modelProfileEditor);
  }

  public async updateProviderDisplayName(value: string): Promise<void> {
    await this.updateDisplayName(
      this.page.getByTestId(TestIds.modelAdministration.providerDisplayName),
      this.page.getByTestId(TestIds.modelAdministration.providerSave),
      value,
      this.page.getByTestId(TestIds.modelAdministration.providerForm),
    );
  }

  public async updateRuntimeDisplayName(value: string): Promise<void> {
    await this.updateDisplayName(
      this.page.getByTestId(TestIds.modelAdministration.runtimeDisplayName),
      this.page.getByTestId(TestIds.modelAdministration.runtimeSave),
      value,
      this.page.getByTestId(TestIds.modelAdministration.runtimeForm),
    );
  }

  public async updateProfileDisplayName(value: string): Promise<void> {
    await this.updateDisplayName(
      this.page.getByTestId(TestIds.modelAdministration.profileDisplayName),
      this.page.getByTestId(TestIds.modelAdministration.profileSave),
      value,
      this.page.getByTestId(TestIds.modelAdministration.profileForm),
    );
  }

  public async deleteProfile(): Promise<void> {
    await this.deleteCurrent(TestIds.modelAdministration.profileDelete, TestIds.modelAdministration.profileDeleteConfirm, '/modelprofiles');
  }

  public async openRuntime(consoleUrl: string, name: string): Promise<void> {
    await this.open(consoleUrl, `/runtimeprofiles/${name}`, TestIds.resourceAdministration.runtimeProfileEditor);
    await this.waitForPersisted(TestIds.modelAdministration.runtimeForm);
  }

  public async deleteRuntime(): Promise<void> {
    await this.deleteCurrent(TestIds.modelAdministration.runtimeDelete, TestIds.modelAdministration.runtimeDeleteConfirm, '/runtimeprofiles');
  }

  public async openProvider(consoleUrl: string, name: string): Promise<void> {
    await this.open(consoleUrl, `/modelproviders/${name}`, TestIds.resourceAdministration.modelProviderEditor);
    await this.openProviderConfiguration();
  }

  public async deleteProvider(): Promise<void> {
    await this.deleteCurrent(TestIds.modelAdministration.providerDelete, TestIds.modelAdministration.providerDeleteConfirm, '/modelproviders');
  }

  private async updateDisplayName(input: Locator, save: Locator, value: string, form: Locator): Promise<void> {
    const previousEtag = await form.getAttribute('data-resource-etag');
    if (!previousEtag) throw new Error('The editor did not expose a persisted resource version.');
    await fillAndCommit(input, value);
    await save.click();
    await expect.poll(() => form.getAttribute('data-resource-etag')).not.toBe(previousEtag);
    await expect(input).toHaveValue(value);
  }

  private async waitForInteractive(testId: string): Promise<void> {
    await this.page.locator(`[data-testid="${testId}"][data-interactive="true"]`).waitFor({ state: 'visible' });
  }

  private async waitForPersisted(testId: string): Promise<void> {
    await this.waitForInteractive(testId);
    await expect(this.page.getByTestId(testId)).toHaveAttribute('data-resource-etag', /.+/);
  }

  private async openProviderConfiguration(): Promise<void> {
    const configurationTab = this.page.getByTestId(TestIds.modelAdministration.providerConfigurationTab);
    const form = this.page.getByTestId(TestIds.modelAdministration.providerForm);
    await expect(async () => {
      await configurationTab.click();
      await expect(configurationTab).toHaveAttribute('aria-selected', 'true');
      await expect(form).toBeVisible();
    }).toPass();
    await this.waitForPersisted(TestIds.modelAdministration.providerForm);
  }

  private async deleteCurrent(deleteButtonId: string, confirmButtonId: string, listPath: string): Promise<void> {
    await this.page.getByTestId(deleteButtonId).click();
    await this.page.getByTestId(confirmButtonId).click();
    await this.page.waitForURL(url => url.pathname === listPath);
  }

  private async open(consoleUrl: string, path: string, marker: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}${path}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Model administration route '${path}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(marker).waitFor({ state: 'visible' });
  }
}

async function selectMatchingOption(select: Locator, text: string): Promise<void> {
  const option = select.locator('option').filter({ hasText: text }).first();
  await option.waitFor({ state: 'attached' });
  const value = await option.getAttribute('value');
  if (!value) throw new Error(`No selectable option contains '${text}'.`);
  await select.selectOption(value);
}

async function selectFirstOption(select: Locator): Promise<void> {
  const option = select.locator('option:not([value=""]):not([disabled])').first();
  await option.waitFor({ state: 'attached' });
  const value = await option.getAttribute('value');
  if (!value) throw new Error('No selectable option is available.');
  await select.selectOption(value);
}
