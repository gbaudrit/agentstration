import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit } from './controls.js';

export class FoundrySecretBindingPage {
  public constructor(private readonly page: Page) {}

  public requirement(id: string): Locator {
    return this.page.locator(`[data-testid="model-provider-value-requirement"][data-requirement-id="${id}"]`);
  }

  public async openNewProvider(consoleUrl: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/modelproviders/new`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Foundry provider route returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.resourceAdministration.modelProviderEditor).waitFor({ state: 'visible' });
    await this.page.locator(`[data-testid="model-provider-form"][data-interactive="true"]`).waitFor({ state: 'visible' });
  }

  public async createProvider(consoleUrl: string, name: string, displayName: string): Promise<void> {
    await this.openNewProvider(consoleUrl);
    await fillAndCommit(this.page.getByTestId('model-provider-name'), name);
    await fillAndCommit(this.page.getByTestId('model-provider-display-name'), displayName);
    await selectMatchingOption(this.page.getByTestId('model-provider-extension'), 'foundry-extension');
    await selectMatchingOption(this.page.getByTestId('model-provider-contribution'), 'microsoft-foundry');
  }

  public async createParameter(requirementId: string, value: string): Promise<void> {
    const panel = this.requirement(requirementId);
    await panel.getByTestId('model-provider-binding-create').click();
    const dialog = this.page.getByTestId('contextual-parameter-creator');
    await dialog.waitFor({ state: 'visible' });
    const valueInput = dialog.getByTestId('contextual-parameter-value');
    if (await valueInput.evaluate(element => element.tagName === 'SELECT')) await valueInput.selectOption({ label: value });
    else await fillAndCommit(valueInput, value);
    await dialog.getByTestId('contextual-parameter-create').click();
    await dialog.waitFor({ state: 'detached' });
  }

  public async createSecret(requirementId: string, value: string): Promise<string> {
    const panel = this.requirement(requirementId);
    await panel.getByTestId('model-provider-binding-kind').selectOption('Secret');
    await panel.getByTestId('model-provider-binding-create').click();
    const dialog = this.page.getByTestId('contextual-secret-creator');
    await dialog.waitFor({ state: 'visible' });
    const name = await dialog.getByTestId('contextual-secret-name').inputValue();
    const vault = dialog.getByTestId('contextual-secret-vault');
    const vaultOption = vault.locator('option:not([value=""])').first();
    await vaultOption.waitFor({ state: 'attached' });
    await vault.selectOption(await vaultOption.getAttribute('value') ?? '');
    await fillAndCommit(dialog.getByTestId('contextual-secret-value'), value);
    const create = dialog.getByTestId('contextual-secret-create');
    await expect(create).toBeEnabled();
    await create.click();
    await dialog.waitFor({ state: 'detached' });
    if ((await this.page.locator('body').textContent())?.includes(value)) throw new Error('The Secret value was rendered in browser-visible text.');
    return name;
  }

  public async selectExistingSecret(requirementId: string, secretName: string): Promise<void> {
    const target = this.requirement(requirementId).getByTestId('model-provider-binding-target');
    const option = target.locator('option').filter({ hasText: secretName }).first();
    await option.waitFor({ state: 'attached' });
    await target.selectOption(await option.getAttribute('value') ?? '');
  }

  public async saveProvider(name: string): Promise<void> {
    await this.page.getByTestId('model-provider-save').click();
    await this.page.waitForURL(url => url.pathname === `/modelproviders/${name}`);
    await this.openConfiguration();
    await this.page.locator(`[data-testid="model-provider-form"][data-interactive="true"]`).waitFor({ state: 'visible' });
  }

  public async openSecret(consoleUrl: string, name: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/secrets/${encodeURIComponent(name)}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Secret route returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.resourceAdministration.secretEditor).waitFor({ state: 'visible' });
    await this.page.locator('.resource-form[data-interactive="true"]').waitFor({ state: 'visible' });
  }

  public async openProvider(consoleUrl: string, name: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/modelproviders/${encodeURIComponent(name)}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Provider route returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.openConfiguration();
    await this.page.locator(`[data-testid="model-provider-form"][data-interactive="true"]`).waitFor({ state: 'visible' });
  }

  private async openConfiguration(): Promise<void> {
    const tab = this.page.locator('[data-testid="model-provider-configuration-tab"][data-interactive="true"]');
    await tab.waitFor({ state: 'visible' });
    await tab.click();
    await expect(tab).toHaveAttribute('aria-selected', 'true');
  }

  public async deleteSecretValue(): Promise<void> {
    const section = this.page.locator('.resource-section').filter({ hasText: /Secret Value|Valeur du secret/i }).last();
    await section.getByRole('button', { name: /Delete value|Supprimer la valeur/i }).click();
    const dialog = this.page.getByRole('alertdialog');
    await dialog.waitFor({ state: 'visible' });
    await dialog.getByRole('button', { name: /Delete value|Supprimer la valeur/i }).click();
    await expect(section).toContainText(/Missing|Manquante/i);
  }

  public async expectUnavailable(requirementId: string): Promise<void> {
    await expect(this.requirement(requirementId).getByTestId('model-provider-binding-target').locator('option:disabled')).toHaveCount(1);
  }
}

async function selectMatchingOption(select: Locator, text: string): Promise<void> {
  const option = select.locator('option').filter({ hasText: text }).first();
  await option.waitFor({ state: 'attached' });
  const value = await option.getAttribute('value');
  if (!value) throw new Error(`No selectable option contains '${text}'.`);
  await select.selectOption(value);
}
