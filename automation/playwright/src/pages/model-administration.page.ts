import { type Locator, type Page } from '@playwright/test';
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
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.providerName), definition.providerName);
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.providerDisplayName), definition.providerDisplayName);
    await selectMatchingOption(this.page.getByTestId(TestIds.modelAdministration.providerExtension), 'ollama-extension');
    await selectFirstOption(this.page.getByTestId(TestIds.modelAdministration.providerContribution));
    await this.page.getByTestId(TestIds.modelAdministration.providerSave).click();
    await this.page.waitForURL(url => url.pathname === `/modelproviders/${definition.providerName}`);
    const marker = this.page.getByTestId(TestIds.resourceAdministration.modelProviderEditor);
    await marker.waitFor({ state: 'visible' });
    await this.page.getByTestId(TestIds.modelAdministration.providerTest).click();
    await this.page.getByTestId(TestIds.modelAdministration.providerStatus).waitFor({ state: 'visible' });
    return marker;
  }

  public async createRuntime(consoleUrl: string, definition: ModelAdministrationDefinition): Promise<Locator> {
    await this.open(consoleUrl, '/runtimeprofiles/new', TestIds.resourceAdministration.runtimeProfileEditor);
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.runtimeName), definition.runtimeName);
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.runtimeDisplayName), definition.runtimeDisplayName);
    await this.page.getByTestId(TestIds.modelAdministration.runtimeSave).click();
    await this.page.waitForURL(url => url.pathname === `/runtimeprofiles/${definition.runtimeName}`);
    return this.page.getByTestId(TestIds.resourceAdministration.runtimeProfileEditor);
  }

  public async createProfile(consoleUrl: string, definition: ModelAdministrationDefinition): Promise<Locator> {
    await this.open(consoleUrl, '/modelprofiles/new', TestIds.resourceAdministration.modelProfileEditor);
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.profileName), definition.profileName);
    await fillAndCommit(this.page.getByTestId(TestIds.modelAdministration.profileDisplayName), definition.profileDisplayName);
    await selectMatchingOption(this.page.getByTestId(TestIds.modelAdministration.profileProvider), definition.providerName);
    await selectFirstOption(this.page.getByTestId(TestIds.modelAdministration.profileModel));
    await this.page.getByTestId(TestIds.modelAdministration.profileSave).click();
    await this.page.waitForURL(url => url.pathname === `/modelprofiles/${definition.profileName}`);
    return this.page.getByTestId(TestIds.resourceAdministration.modelProfileEditor);
  }

  public async updateProviderDisplayName(value: string): Promise<void> {
    await this.updateDisplayName(
      this.page.getByTestId(TestIds.modelAdministration.providerDisplayName),
      this.page.getByTestId(TestIds.modelAdministration.providerSave),
      value,
    );
  }

  public async updateRuntimeDisplayName(value: string): Promise<void> {
    await this.updateDisplayName(
      this.page.getByTestId(TestIds.modelAdministration.runtimeDisplayName),
      this.page.getByTestId(TestIds.modelAdministration.runtimeSave),
      value,
    );
  }

  public async updateProfileDisplayName(value: string): Promise<void> {
    await this.updateDisplayName(
      this.page.getByTestId(TestIds.modelAdministration.profileDisplayName),
      this.page.getByTestId(TestIds.modelAdministration.profileSave),
      value,
    );
  }

  public async deleteProfile(): Promise<void> {
    await this.deleteCurrent(TestIds.modelAdministration.profileDelete, TestIds.modelAdministration.profileDeleteConfirm, '/modelprofiles');
  }

  public async openRuntime(consoleUrl: string, name: string): Promise<void> {
    await this.open(consoleUrl, `/runtimeprofiles/${name}`, TestIds.resourceAdministration.runtimeProfileEditor);
  }

  public async deleteRuntime(): Promise<void> {
    await this.deleteCurrent(TestIds.modelAdministration.runtimeDelete, TestIds.modelAdministration.runtimeDeleteConfirm, '/runtimeprofiles');
  }

  public async openProvider(consoleUrl: string, name: string): Promise<void> {
    await this.open(consoleUrl, `/modelproviders/${name}`, TestIds.resourceAdministration.modelProviderEditor);
  }

  public async deleteProvider(): Promise<void> {
    await this.deleteCurrent(TestIds.modelAdministration.providerDelete, TestIds.modelAdministration.providerDeleteConfirm, '/modelproviders');
  }

  private async updateDisplayName(input: Locator, save: Locator, value: string): Promise<void> {
    await fillAndCommit(input, value);
    const response = this.page.waitForResponse(candidate => candidate.request().method() === 'PUT' && candidate.ok());
    await save.click();
    await response;
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
