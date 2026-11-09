import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
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
    await this.page.locator(`[data-testid="${TestIds.agentEditor.form}"][data-interactive="true"]`).waitFor({ state: 'visible' });
  }

  public async open(consoleUrl: string, namespace: string, name: string): Promise<void> {
    const response = await this.page.goto(
      await this.contextualUrl(`${consoleUrl}/agents/${encodeURIComponent(name)}?namespace=${encodeURIComponent(namespace)}`),
      { waitUntil: 'domcontentloaded' },
    );
    if (!response?.ok()) throw new Error(`Agent page returned HTTP ${response?.status() ?? 'no response'}.`);
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
    await this.page.getByTestId(TestIds.agentEditor.toolsTab).click();
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
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.instructions), agent.instructions);
  }

  public async createAndDeploy(name: string): Promise<void> {
    await Promise.all([
      this.page.waitForURL(url => url.pathname === `/agents/${encodeURIComponent(name)}`),
      this.page.getByTestId(TestIds.agentEditor.createAndDeploy).click(),
    ]);
    await this.openEdit();
    await this.page.getByTestId(TestIds.agentEditor.form).waitFor({ state: 'visible' });
    await expect(this.form).toHaveAttribute('data-interactive', 'true');
  }

  public async openEdit(): Promise<void> {
    const tab = this.page.getByTestId(TestIds.agentEditor.editTab);
    await expect(tab).toBeVisible();
    await expect(async () => {
      await tab.click();
      await expect(tab).toHaveAttribute('aria-selected', 'true');
      await expect(this.form).toBeVisible();
    }).toPass();
  }

  public async selectModelProfile(selection: ProfileSelection): Promise<void> {
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

  private async contextualUrl(url: string): Promise<string> {
    const target = new URL(url);
    const scopeRef = await this.page.getByTestId(TestIds.console.shell)
      .getAttribute('data-context-scope')
      .catch(() => null);
    if (scopeRef && !target.searchParams.has('context')) target.searchParams.set('context', scopeRef);
    return target.toString();
  }
}
