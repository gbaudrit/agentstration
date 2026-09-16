import type { Locator, Page } from '@playwright/test';
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
    const response = await this.page.goto(`${consoleUrl}/agents/new`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`New agent page returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.locator(`[data-testid="${TestIds.agentEditor.form}"][data-interactive="true"]`).waitFor({ state: 'visible' });
  }

  public async fillIdentity(agent: Pick<AgentDefinition, 'name' | 'displayName' | 'description'>): Promise<void> {
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.name), agent.name);
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.displayName), agent.displayName);
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.description), agent.description);
  }

  public async configureBehavior(agent: Pick<AgentDefinition, 'instructions' | 'modelProfile' | 'runtimeProfile'>): Promise<void> {
    await selectFirstAvailable(this.page.getByTestId(TestIds.agentEditor.modelProfile), agent.modelProfile, 'Model profile');
    await selectFirstAvailable(this.page.getByTestId(TestIds.agentEditor.runtimeProfile), agent.runtimeProfile, 'Runtime profile');
    await fillAndCommit(this.page.getByTestId(TestIds.agentEditor.instructions), agent.instructions);
  }

  public async createAndDeploy(name: string): Promise<void> {
    await Promise.all([
      this.page.waitForURL(url => url.pathname === `/agents/${encodeURIComponent(name)}`),
      this.page.getByTestId(TestIds.agentEditor.createAndDeploy).click(),
    ]);
    await this.page.getByTestId(TestIds.agentEditor.form).waitFor({ state: 'visible' });
    await this.page.getByTestId(TestIds.agentEditor.name).waitFor({ state: 'visible' });
  }

  public get form(): Locator { return this.page.getByTestId(TestIds.agentEditor.form); }
  public get identitySection(): Locator { return this.page.getByTestId(TestIds.agentEditor.identitySection); }
  public get behaviorSection(): Locator { return this.page.getByTestId(TestIds.agentEditor.behaviorSection); }
}
