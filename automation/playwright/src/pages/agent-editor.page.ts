import type { Locator, Page } from '@playwright/test';

export type ProfileSelection = string | readonly string[];

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
    const response = await this.page.goto(`${consoleUrl}/agents/new`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`New agent page returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.locator('[data-testid="agent-editor-form"][data-interactive="true"]').waitFor({ state: 'visible' });
  }

  public async fillIdentity(agent: Pick<AgentDefinition, 'name' | 'displayName' | 'description'>): Promise<void> {
    await fillAndCommit(this.page.getByTestId('agent-name'), agent.name);
    await fillAndCommit(this.page.getByTestId('agent-display-name'), agent.displayName);
    await fillAndCommit(this.page.getByTestId('agent-description'), agent.description);
  }

  public async configureBehavior(agent: Pick<AgentDefinition, 'instructions' | 'modelProfile' | 'runtimeProfile'>): Promise<void> {
    await selectFirstAvailable(this.page.getByTestId('model-profile-select'), agent.modelProfile, 'Model profile');
    await selectFirstAvailable(this.page.getByTestId('agent-runtime-profile'), agent.runtimeProfile, 'Runtime profile');
    await fillAndCommit(this.page.getByTestId('agent-instructions'), agent.instructions);
  }

  public async createAndDeploy(name: string): Promise<void> {
    await Promise.all([
      this.page.waitForURL(url => url.pathname === `/agents/${encodeURIComponent(name)}`),
      this.page.getByTestId('agent-create-and-deploy').click(),
    ]);
    await this.page.getByTestId('agent-editor-form').waitFor({ state: 'visible' });
    await this.page.getByTestId('agent-name').waitFor({ state: 'visible' });
  }
}

async function fillAndCommit(locator: ReturnType<Page['getByTestId']>, value: string): Promise<void> {
  await locator.fill(value);
  await locator.blur();
}

async function selectFirstAvailable(locator: Locator, selection: ProfileSelection, label: string): Promise<void> {
  await locator.waitFor({ state: 'visible' });
  const candidates = typeof selection === 'string' ? [selection] : [...selection];
  const options = await locator.locator('option').evaluateAll(elements => elements.map(element => ({
    value: (element as HTMLOptionElement).value,
    label: element.textContent?.trim() ?? '',
    disabled: (element as HTMLOptionElement).disabled,
  })));
  const selected = candidates.find(candidate => options.some(option => option.value === candidate && !option.disabled));
  if (!selected) {
    const available = options.filter(option => option.value && !option.disabled).map(option => `${option.value} (${option.label})`);
    throw new Error(`${label} candidates [${candidates.join(', ')}] are unavailable. Available options: ${available.join(', ') || 'none'}.`);
  }
  await locator.selectOption(selected);
}
