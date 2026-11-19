import { selectResourceSection } from './resource-sections.js';
import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { AgentOverviewMetricsByLocale, AgentValidationTextByLocale, ResourceSectionTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';
import { fillAndCommit, selectFirstAvailable, type ControlSelection } from './controls.js';

export type ProfileSelection = ControlSelection;

export interface AgentDefinition {
  name: string;
  displayName: string;
  description: string;
  instructions: string;
  modelProfile: ProfileSelection;
  runtimeProfile: ProfileSelection;
}

export class AgentEditorPage {
  public constructor(private readonly page: Page) {}

  public async openNew(consoleUrl: string): Promise<void> {
    const response = await this.page.goto(
      await this.contextualUrl(`${consoleUrl}/agents/new`),
      { waitUntil: 'domcontentloaded' },
    );
    if (!response?.ok()) throw new Error(`New agent page returned HTTP ${response?.status() ?? 'no response'}.`);
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-preferences-ready', 'true');
    await this.page.locator(`[data-testid="${TestIds.agentEditor.form}"][data-interactive="true"]`).waitFor({ state: 'visible' });
  }

  public async open(consoleUrl: string, namespace: string, name: string): Promise<void> {
    const response = await this.page.goto(
      await this.contextualUrl(`${consoleUrl}/agents/${encodeURIComponent(name)}?namespace=${encodeURIComponent(namespace)}`),
      { waitUntil: 'domcontentloaded' },
    );
    if (!response?.ok()) throw new Error(`Agent page returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.agentEditor.overview).waitFor({ state: 'visible' });
    await this.openEdit();
    await this.form.waitFor({ state: 'visible' });
    await expect(this.form).toHaveAttribute('data-interactive', 'true');
  }

  public async saveDraft(): Promise<void> {
    await this.page.getByTestId(TestIds.agentEditor.saveDraft).click();
  }

  public async openTools(): Promise<void> {
    if (!await this.form.isVisible()) await this.openEdit();
    await expect(this.form).toHaveAttribute('data-interactive', 'true');
    if (await this.page.getByTestId("agent-sections").count()) await this.selectSection("definition/tools");
    else await this.selectCreationStep('tools');
    await this.page.getByTestId(TestIds.agentEditor.toolSetConfiguration).waitFor({ state: 'visible' });
    await this.page.getByTestId(TestIds.agentEditor.toolConfiguration).waitFor({ state: 'visible' });
  }

  public async fillIdentity(agent: Pick<AgentDefinition, 'name' | 'displayName' | 'description'>): Promise<void> {
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.name), agent.name);
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.displayName), agent.displayName);
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.description), agent.description);
  }

  public async configureBehavior(agent: Pick<AgentDefinition, 'instructions' | 'modelProfile' | 'runtimeProfile'>): Promise<void> {
    await this.selectModelProfile(agent.modelProfile);
    await selectFirstAvailable(this.page.getByTestId(TestIds.agentEditor.runtimeProfile), agent.runtimeProfile, 'Runtime profile');
    if (await this.page.getByTestId("agent-sections").count()) await this.openConfiguration();
    else await this.selectCreationStep('configuration');
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.instructions), agent.instructions);
  }

  public async createAndDeploy(name: string): Promise<void> {
    await this.selectCreationStep('review');
    const submit = this.page.getByTestId(TestIds.agentEditor.createAndDeploy);
    if ((this.page.viewportSize()?.width ?? 1440) <= 620) {
      await submit.scrollIntoViewIfNeeded();
      const button = await submit.boundingBox();
      const navigation = await this.page.getByTestId(TestIds.console.sidebar).boundingBox();
      expect(button).not.toBeNull();
      expect(navigation).not.toBeNull();
      expect(button!.y + button!.height, 'The primary action must clear the fixed mobile navigation').toBeLessThanOrEqual(navigation!.y);
    }
    await Promise.all([
      this.page.waitForURL(url => url.pathname === `/agents/${encodeURIComponent(name)}`),
      submit.click(),
    ]);
    await this.page.getByTestId(TestIds.agentEditor.overview).waitFor({ state: 'visible' });
    await this.openEdit();
    await this.page.getByTestId(TestIds.agentEditor.form).waitFor({ state: 'visible' });
    await expect(this.form).toHaveAttribute('data-interactive', 'true');
  }

  public async selectCreationStep(step: 'identity' | 'configuration' | 'model-execution' | 'tools' | 'contracts' | 'review'): Promise<void> {
    await this.page.locator(`[data-wizard-step="${step}"]`).click();
    await expect(this.page.locator(`[data-step-id="${step}"]`)).toBeVisible();
  }

  public async selectSection(path: string): Promise<void> {
    await selectResourceSection(this.page, path);
  }

  public async openOverview(): Promise<void> {
    await this.selectSection('overview');
    await expect(this.page.getByTestId(TestIds.agentEditor.overview)).toBeVisible();
  }

  public async expectStandardOverview(locale: SupportedTestLocale): Promise<void> {
    const overview = this.page.getByTestId(TestIds.agentEditor.overview);
    const cards = overview.locator('.resource-overview-metrics .metric-card');
    await expect(cards).toHaveCount(3);
    await expect(cards.locator('.metric-label')).toHaveText([...AgentOverviewMetricsByLocale[locale]]);
    await expect(overview.locator('.state-comparison')).toHaveCount(0);
    const labels = overview.locator('.resource-identity-summary .labels-summary');
    await expect(labels).toBeVisible();
    await expect(overview.locator('.labels-summary')).toHaveCount(1);
    await expect(overview.locator('.resource-identity-summary dt').filter({ hasText: ResourceSectionTextByLocale[locale].labels })).toBeVisible();
    const metrics = await overview.locator('.resource-overview-metrics').boundingBox();
    const identity = await overview.locator('.resource-identity-summary').boundingBox();
    const configuration = await overview.locator('.resource-configuration-summary').boundingBox();
    expect(metrics).not.toBeNull(); expect(identity).not.toBeNull(); expect(configuration).not.toBeNull();
    expect(metrics!.y + metrics!.height).toBeLessThan(identity!.y);
    expect(identity!.y + identity!.height).toBeLessThan(configuration!.y);
  }

  public async expectDeclaredSectionPresentation(locale: SupportedTestLocale): Promise<void> {
    const text = ResourceSectionTextByLocale[locale];
    const legend = this.page.locator('.resource-details-guidance .required-fields-note');
    await this.openEdit();
    await expect(legend).toHaveText(text.required);
    await expect(this.identitySection.getByRole('heading', { name: text.identity, exact: true })).toBeVisible();
    await expect(this.identitySection.locator('.form-field-heading > label').filter({ hasText: text.labels })).toBeVisible();
    await expect(this.identitySection.getByTestId('labels-editor')).toBeVisible();
    await expect(this.page.getByTestId('agent-labels-section')).toHaveCount(0);
    await expect(this.behaviorSection).not.toBeVisible();
    await this.openConfiguration();
    await expect(legend).toHaveText(text.required);
    await expect(this.identitySection).not.toBeVisible();
    await expect(this.page.locator('.resource-header .required-fields-note')).toHaveCount(0);
    await this.selectSection('definition/model-execution');
    await expect(legend).toHaveText(text.required);
    await this.expectPanelSpacing([TestIds.agentEditor.modelSection, TestIds.agentEditor.runtimeSection]);
    await this.selectSection('definition/contracts');
    await expect(legend).toHaveCount(0);
    await this.selectSection('yaml');
    await expect(legend).toHaveCount(0);
    const actions = this.page.getByRole('group', { name: text.actions, exact: true });
    const apply = actions.getByRole('button', { name: text.applyYaml, exact: true });
    await expect(apply).toBeEnabled();
    await expect(this.page.getByTestId(TestIds.agentEditor.yamlEditor).getByRole('button', { name: text.applyYaml, exact: true })).toHaveCount(0);
    await apply.scrollIntoViewIfNeeded();
    if ((this.page.viewportSize()?.width ?? 1440) <= 620) {
      const button = await apply.boundingBox();
      const navigation = await this.page.getByTestId(TestIds.console.sidebar).boundingBox();
      expect(button!.y + button!.height).toBeLessThanOrEqual(navigation!.y);
    }
    await apply.click();
  }

  public async expectLocalizedInlineValidation(locale: SupportedTestLocale): Promise<void> {
    await this.openEdit();
    const displayName = this.page.getByTestId(TestIds.agentEditor.displayName);
    const savedName = await displayName.inputValue();
    const nameField = this.page.locator('.form-field').filter({ has: displayName });
    await displayName.fill('');
    await displayName.blur();
    await expect(nameField.locator('.validation-message')).toHaveText(AgentValidationTextByLocale[locale].displayNameRequired);
    await this.expectInvalidBorder(displayName, nameField.locator('.validation-message'));
    await this.openOverview();
    await this.saveDraft();
    await expect(this.page).toHaveURL(url => url.hash === '#definition/identity');
    await expect(displayName).toBeFocused();
    await displayName.fill(savedName);
    await displayName.blur();
    await expect(displayName).not.toHaveClass(/\binvalid\b/);
    await expect(nameField.locator('.validation-message')).toHaveCount(0);
    await this.openConfiguration();
    const instructions = this.page.getByTestId(TestIds.agentEditor.instructions);
    const original = await instructions.inputValue();
    const field = this.page.locator('.form-field').filter({ has: instructions });
    await instructions.fill('');
    await instructions.blur();
    await expect(field.locator('.validation-message')).toHaveText(AgentValidationTextByLocale[locale].instructionsRequired);
    await this.expectInvalidBorder(instructions, field.locator('.validation-message'));
    const messagesId = await field.locator('.form-field-messages').getAttribute('id');
    await expect(instructions).toHaveAttribute('aria-describedby', messagesId!);
    await this.openOverview();
    await this.saveDraft();
    await expect(this.page).toHaveURL(url => url.hash === '#definition/configuration');
    await expect(instructions).toBeFocused();
    await expect(field.locator('.validation-message')).toHaveText(AgentValidationTextByLocale[locale].instructionsRequired);
    await this.refresh();
    await expect(instructions).toHaveValue('');
    await expect(field.locator('.validation-message')).toHaveText(AgentValidationTextByLocale[locale].instructionsRequired);
    await instructions.fill(original);
    await instructions.blur();
    await expect(field.locator('.validation-message')).toHaveCount(0);
    await expect(this.page.locator('.unsaved-changes-bar')).toHaveCount(0);
  }

  public async openEdit(): Promise<void> {
    await this.selectSection('definition/identity');
    await expect(this.form).toBeVisible();
    await expect(this.identitySection).toBeVisible();
  }

  public async openConfiguration(): Promise<void> {
    await this.selectSection('definition/configuration');
    await expect(this.behaviorSection).toBeVisible();
  }

  public async prepareIncompleteLabel(): Promise<void> {
    await this.identitySection.getByTestId('label-add').click();
    await this.identitySection.getByTestId('label-key').fill('environment');
    await this.identitySection.getByTestId('label-key').blur();
    await this.openConfiguration();
    await this.openEdit();
    await this.expectIncompleteLabel();
  }

  public async expectIncompleteLabel(): Promise<void> {
    await expect(this.identitySection.getByTestId('label-key')).toHaveValue('environment');
    await expect(this.identitySection.locator('.label-row-error')).toBeVisible();
  }

  public async expectNoLabelDraft(): Promise<void> {
    await expect(this.identitySection.getByTestId('label-key')).toHaveCount(0);
  }

  public async refresh(): Promise<void> {
    const button = this.page.getByTestId(TestIds.agentEditor.refresh);
    await button.click();
    await expect(button).toBeEnabled();
    await expect(this.page.locator('.resource-refresh-status, .resource-refresh-error')).toHaveCount(0);
  }

  public async selectModelProfile(selection: ProfileSelection): Promise<void> {
    if (await this.page.getByTestId("agent-sections").count()) await this.selectSection("definition/model-execution");
    else await this.selectCreationStep('model-execution');
    const candidates = typeof selection === 'string' ? [selection] : [...selection];
    const toggle = this.page.getByTestId(TestIds.agentEditor.modelProfileToggle);
    if (await toggle.getAttribute('aria-expanded') !== 'true') await toggle.click();
    for (const candidate of candidates) {
      const name = candidate.split('|').at(-1)!;
      await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.modelProfileSearch), name);
      const choice = this.page.getByTestId(TestIds.agentEditor.modelProfileChoice)
        .and(this.page.locator(`[data-profile="${candidate}"]`));
      try { await expect(choice).toBeEnabled({ timeout: 5000 }); }
      catch { continue; }
      await expect(choice).toBeVisible();
      await expect(choice.locator('xpath=ancestor::details')).toHaveCount(0);
      await choice.click(); return;
    }
    throw new Error(`No available model profile matches [${candidates.join(', ')}].`);
  }

  public async expectModelProfileUnavailable(value: string): Promise<void> {
    const toggle = this.page.getByTestId(TestIds.agentEditor.modelProfileToggle);
    if (await toggle.getAttribute('aria-expanded') !== 'true') await toggle.click();
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.modelProfileSearch), value.split('|').at(-1)!);
    const choice = this.page.getByTestId(TestIds.agentEditor.modelProfileChoice)
      .and(this.page.locator(`[data-profile="${value}"]`));
    if (await choice.count()) await expect(choice).toBeDisabled();
  }

  public get form(): Locator { return this.page.getByTestId(TestIds.agentEditor.form); }
  public get identitySection(): Locator { return this.page.getByTestId(TestIds.agentEditor.identitySection); }
  public get behaviorSection(): Locator { return this.page.getByTestId(TestIds.agentEditor.behaviorSection); }

  private async expectInvalidBorder(control: Locator, message: Locator): Promise<void> {
    await expect(control).toHaveClass(/\binvalid\b/);
    const errorColor = await message.evaluate(node => getComputedStyle(node).color);
    await this.page.mouse.move(0, 0);
    await control.blur();
    await expect.poll(() => control.evaluate(node => getComputedStyle(node).borderTopColor)).toBe(errorColor);
    await control.focus();
    await expect.poll(() => control.evaluate(node => getComputedStyle(node).borderTopColor)).toBe(errorColor);
    await control.hover();
    await expect.poll(() => control.evaluate(node => getComputedStyle(node).borderTopColor)).toBe(errorColor);
    await control.blur();
  }

  private async expectPanelSpacing(ids: readonly string[]): Promise<void> {
    for (let index = 1; index < ids.length; index++) {
      const previous = this.page.getByTestId(ids[index - 1]!);
      await expect(previous).toBeVisible();
      await expect(this.page.getByTestId(ids[index]!)).toBeVisible();
      const spacing = await previous.evaluate((panel, nextId) => {
        const next = document.querySelector(`[data-testid="${nextId}"]`)!;
        return {
          actual: next.getBoundingClientRect().top - panel.getBoundingClientRect().bottom,
          expected: Number.parseFloat(getComputedStyle(panel.parentElement!).rowGap),
        };
      }, ids[index]!);
      expect(spacing.expected).toBeGreaterThan(0);
      expect(Math.abs(spacing.actual - spacing.expected), 'Adjacent panels use the shared section gap').toBeLessThanOrEqual(1);
    }
  }

  private async contextualUrl(url: string): Promise<string> {
    const target = new URL(url);
    const scopeRef = await this.page.getByTestId(TestIds.console.shell)
      .getAttribute('data-context-scope')
      .catch(() => null);
    if (scopeRef && !target.searchParams.has('context')) target.searchParams.set('context', scopeRef);
    return target.toString();
  }
}
