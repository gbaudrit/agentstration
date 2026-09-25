import { expect, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit } from './controls.js';

export class ResourceNamingPage {
  public constructor(private readonly page: Page) {}

  public async openAgent(consoleUrl: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/agents/new`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`New agent page returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.locator(`[data-testid="${TestIds.agentEditor.form}"][data-interactive="true"]`).waitFor({ state: 'visible' });
  }

  public async assertIdentityOrderAndFocus(): Promise<void> {
    const displayName = this.page.getByTestId(TestIds.agentEditor.displayName);
    const technicalName = this.page.getByTestId(TestIds.agentEditor.name);
    await expect(displayName).toHaveAttribute('autofocus', '');
    const order = await displayName.evaluate((element, technicalId) => {
      const fields = [...element.closest('.form-grid')!.querySelectorAll<HTMLInputElement>('input')];
      return fields.indexOf(element as HTMLInputElement) < fields.findIndex(field => field.getAttribute('data-testid') === technicalId);
    }, TestIds.agentEditor.name);
    expect(order).toBe(true);
    await expect(technicalName).toBeEditable();
  }

  public async deriveAgentName(displayName: string, expectedTechnicalName: string): Promise<void> {
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.displayName), displayName);
    await expect(this.page.getByTestId(TestIds.agentEditor.name)).toHaveValue(expectedTechnicalName);
  }

  public async overrideAgentName(technicalName: string, displayName: string): Promise<void> {
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.name), technicalName);
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.displayName), displayName);
    await expect(this.page.getByTestId(TestIds.agentEditor.name)).toHaveValue(technicalName);
  }

  public async openParameter(consoleUrl: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/parameters/new`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`New parameter page returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.parameterEditor.form).waitFor({ state: 'visible' });
  }

  public async deriveParameterName(displayName: string, expectedTechnicalName: string): Promise<void> {
    const display = this.page.getByTestId(TestIds.parameterEditor.displayName);
    const technical = this.page.getByTestId(TestIds.parameterEditor.technicalName);
    await expect(async () => {
      await fillAndCommit(display, displayName);
      await expect(technical).toHaveValue(expectedTechnicalName);
    }).toPass();
  }

  public async overrideAndClearParameterName(prefilledName: string, changedDisplayName: string): Promise<void> {
    await fillAndCommit(this.page.getByTestId(TestIds.parameterEditor.technicalName), prefilledName);
    await fillAndCommit(this.page.getByTestId(TestIds.parameterEditor.displayName), changedDisplayName);
    await expect(this.page.getByTestId(TestIds.parameterEditor.technicalName)).toHaveValue(prefilledName);
    await this.page.getByTestId(TestIds.parameterEditor.technicalName).fill('');
    await this.page.getByTestId(TestIds.parameterEditor.technicalName).blur();
    await fillAndCommit(this.page.getByTestId(TestIds.parameterEditor.displayName), `${changedDisplayName} renamed`);
    await expect(this.page.getByTestId(TestIds.parameterEditor.technicalName)).toHaveValue('');
  }

  public async saveParameter(technicalName: string, value = '6'): Promise<string> {
    await fillAndCommit(this.page.getByTestId(TestIds.parameterEditor.technicalName), technicalName);
    await fillAndCommit(this.page.getByTestId(TestIds.parameterEditor.value), value);
    await Promise.all([
      this.page.waitForURL(url => url.pathname === `/parameters/${encodeURIComponent(technicalName)}`),
      this.page.getByTestId(TestIds.parameterEditor.save).click(),
    ]);
    await this.page.getByTestId(TestIds.parameterEditor.form).waitFor({ state: 'visible' });
    return this.page.url();
  }

  public async reopenParameter(url: string, expectedTechnicalName: string): Promise<void> {
    const response = await this.page.goto(url, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Parameter edit route returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.parameterEditor.form).waitFor({ state: 'visible' });
    await expect(this.page.getByTestId(TestIds.parameterEditor.technicalName)).toHaveValue(expectedTechnicalName);
    await expect(this.page.getByTestId(TestIds.parameterEditor.technicalName)).toBeDisabled();
    await fillAndCommit(this.page.getByTestId(TestIds.parameterEditor.displayName), 'Retry.Count edited');
    await expect(this.page.getByTestId(TestIds.parameterEditor.technicalName)).toHaveValue(expectedTechnicalName);
    await this.page.getByTestId(TestIds.parameterEditor.save).click();
    await expect(this.page.getByTestId(TestIds.parameterEditor.technicalName)).toHaveValue(expectedTechnicalName);
  }

  public async prepareDuplicateParameter(technicalName: string): Promise<void> {
    await fillAndCommit(this.page.getByTestId(TestIds.parameterEditor.technicalName), technicalName);
    await fillAndCommit(this.page.getByTestId(TestIds.parameterEditor.value), '7');
  }

  public async saveDuplicateParameter(): Promise<void> {
    await this.page.getByTestId(TestIds.parameterEditor.save).click();
    await this.page.locator('.form-alert-danger[role="alert"]').waitFor({ state: 'visible' });
  }

  public get agentIdentity() { return this.page.getByTestId(TestIds.agentEditor.identitySection); }
  public get parameterForm() { return this.page.getByTestId(TestIds.parameterEditor.form); }
}
