import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

export type SecretResourceScope = 'tenant' | 'workspace';

export class DescendantSecretsPage {
  public constructor(private readonly page: Page) {}

  public get vaultEditor(): Locator { return this.page.getByTestId(TestIds.resourceAdministration.vaultEditor); }
  public get secretEditor(): Locator { return this.page.getByTestId(TestIds.resourceAdministration.secretEditor); }

  public async openNewVault(consoleUrl: string): Promise<void> {
    await this.open(`${consoleUrl}/vaults/new`, this.vaultEditor);
  }

  public async openVault(consoleUrl: string, name: string, scopeRef: string): Promise<void> {
    await this.open(`${consoleUrl}/vaults/${encodeURIComponent(name)}?scopeRef=${encodeURIComponent(scopeRef)}`, this.vaultEditor);
  }

  public async openNewSecret(consoleUrl: string): Promise<void> {
    await this.open(`${consoleUrl}/secrets/new`, this.secretEditor);
  }

  public async openSecret(consoleUrl: string, name: string, scopeRef: string): Promise<void> {
    await this.open(`${consoleUrl}/secrets/${encodeURIComponent(name)}?scopeRef=${encodeURIComponent(scopeRef)}`, this.secretEditor);
  }

  public async selectScope(kind: SecretResourceScope): Promise<string> {
    const picker = this.page.getByTestId(TestIds.secretGrants.scopePicker);
    const prefix = kind === 'tenant' ? '/tenants/' : '/workspaces/';
    await picker.locator(`option[value^="${prefix}"]`).first().waitFor({ state: 'attached' });
    const options = await picker.locator('option').evaluateAll(elements => elements.map(element => ({
      value: (element as HTMLOptionElement).value,
      disabled: (element as HTMLOptionElement).disabled,
    })));
    const scopeRef = options.find(option => option.value.startsWith(prefix) && !option.disabled)?.value;
    if (!scopeRef) throw new Error(`No writable ${kind} scope is available in the resource editor.`);
    await picker.selectOption(scopeRef);
    return scopeRef;
  }

  public async createVault(name: string, displayName: string): Promise<void> {
    await this.page.getByTestId(TestIds.secretGrants.vaultName).fill(name);
    await this.page.getByTestId(TestIds.secretGrants.vaultDisplayName).fill(displayName);
    await this.saveNew('vaults', name);
  }

  public async initializeVault(): Promise<void> {
    await this.page.locator('.resource-form[data-interactive="true"]').waitFor({ state: 'visible' });
    const initialize = this.page.locator('.resource-form .form-section .button-primary');
    await initialize.waitFor({ state: 'visible' });
    await initialize.click();
    const confirmation = this.page.getByRole('alertdialog');
    await confirmation.waitFor({ state: 'visible' });
    await confirmation.locator('.button-danger').click();
    await confirmation.waitFor({ state: 'detached' });
    await initialize.waitFor({ state: 'detached' });
  }

  public async createSecret(name: string, displayName: string, vaultName: string, vaultScopeRef: string): Promise<void> {
    const displayNameInput = this.page.getByTestId(TestIds.secretGrants.secretDisplayName);
    await displayNameInput.fill(displayName);
    await displayNameInput.press('Tab');
    await expect(this.page.getByTestId(TestIds.secretGrants.secretName)).toHaveValue(name);
    await expect(this.page.getByTestId(TestIds.secretGrants.secretVaultKey)).toHaveValue(name);
    const option = await this.vaultOption(vaultName, vaultScopeRef);
    if (!option) throw new Error(`Vault '${vaultName}' at '${vaultScopeRef}' is not offered to this Secret.`);
    await this.page.getByTestId(TestIds.secretGrants.secretVault).selectOption(option);
    await this.saveNew('secrets', name);
  }

  public async vaultIsOffered(name: string, scopeRef: string): Promise<boolean> {
    return (await this.vaultOption(name, scopeRef)) !== undefined;
  }

  public async selectedVault(): Promise<string> {
    return this.page.getByTestId(TestIds.secretGrants.secretVault).inputValue();
  }

  public async addGrant(scopeRef: string): Promise<void> {
    const target = this.page.getByTestId(TestIds.secretGrants.grantTarget);
    await target.selectOption(scopeRef);
    await this.page.getByTestId(TestIds.secretGrants.grantAdd).click();
    await this.grantRow(scopeRef).waitFor({ state: 'visible' });
  }

  public async removeGrant(scopeRef: string): Promise<void> {
    await this.grantRow(scopeRef).getByTestId(TestIds.secretGrants.grantRemove).click();
    await this.grantRow(scopeRef).waitFor({ state: 'detached' });
  }

  public grantRow(scopeRef: string): Locator {
    return this.page.locator(`[data-testid="${TestIds.secretGrants.grantRow}"][data-scope-ref="${scopeRef}"]`);
  }

  public scopeBadge(scopeRef: string): Locator {
    return this.page.locator(`.resource-form .resource-scope-badge[title="${scopeRef}"]`);
  }

  public async saveChanges(): Promise<void> {
    const bar = this.page.locator('.resource-form .unsaved-changes-bar');
    await bar.waitFor({ state: 'visible' });
    await bar.locator('button[type="submit"]').click();
    await bar.waitFor({ state: 'hidden' });
  }

  private async vaultOption(name: string, scopeRef: string): Promise<string | undefined> {
    const options = await this.page.getByTestId(TestIds.secretGrants.secretVault).locator('option').evaluateAll(elements =>
      elements.map(element => (element as HTMLOptionElement).value));
    return options.find(value => value === `${scopeRef}|default|${name}`);
  }

  private async saveNew(kind: 'vaults' | 'secrets', name: string): Promise<void> {
    await this.page.locator('.resource-form button[type="submit"]').click();
    await this.page.waitForURL(url => url.pathname === `/${kind}/${name}`);
    await (kind === 'vaults' ? this.vaultEditor : this.secretEditor).waitFor({ state: 'visible' });
  }

  private async open(url: string, marker: Locator): Promise<void> {
    const response = await this.page.goto(url, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Secret management route '${url}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await marker.waitFor({ state: 'visible' });
    await this.page.locator('.resource-form[data-interactive="true"]').waitFor({ state: 'visible' });
  }
}
