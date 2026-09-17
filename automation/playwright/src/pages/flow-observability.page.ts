import { expect, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit } from './controls.js';

type FlowObservabilityMarker = keyof Pick<typeof TestIds.flowObservability,
  'flows' | 'flowDetails' | 'designer' | 'runs' | 'runDetails' | 'agentRunner' |
  'agentRuns' | 'runEvents' | 'tasks' | 'taskDetails' | 'taskRunDetails'>;

export class FlowObservabilityPage {
  public constructor(private readonly page: Page) {}

  public async open(consoleUrl: string, path: string, marker: FlowObservabilityMarker): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}${path}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Flow observability route '${path}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.flowObservability[marker]).waitFor({ state: 'visible' });
  }

  public async exerciseDraftDefinitionAndSplit(consoleUrl: string, checkInvalidYaml: boolean): Promise<void> {
    const flowName = `definition-editor-smoke-${Date.now()}`;
    const created = await this.page.request.post(`${consoleUrl}/api/flows/drafts`, {
      data: { name: flowName, displayName: 'Definition editor smoke', template: 'Empty' },
    });
    expect(created.status(), await created.text()).toBe(201);
    await this.open(consoleUrl, `/flows/${flowName}/designer`, 'designer');

    const shell = this.page.locator('.flow-editor-shell.definition');
    await expect(async () => {
      await this.page.getByRole('button', { name: 'Definition', exact: true }).click();
      await expect(shell).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
    const source = shell.locator('.source-editor');
    const lines = source.locator('.view-lines');
    await expect(lines).toContainText('entryStep');
    await expect(lines).not.toContainText('sourceText');
    await expect(source.locator('.error-panel')).toHaveCount(0);
    await expect(shell.locator('.flow-canvas-wrap')).toHaveCount(0);
    const shellWidth = (await shell.boundingBox())?.width ?? 0;
    const sourceWidth = (await source.boundingBox())?.width ?? 0;
    expect(sourceWidth).toBeGreaterThan(shellWidth * 0.9);

    await source.getByRole('textbox', { name: 'Editor content' }).focus();
    await this.page.keyboard.press('ControlOrMeta+End');
    await this.page.keyboard.insertText('\n# definition-editor-smoke');
    await expect(lines).toContainText('definition-editor-smoke');
    await source.getByRole('button', { name: 'Apply and save' }).click();
    await expect(source.locator('.error-panel')).toHaveCount(0);

    await this.page.getByRole('button', { name: 'Split', exact: true }).click();
    const split = this.page.locator('.flow-editor-shell.split');
    await expect(split).toBeVisible();
    await expect(split.locator('.flow-canvas-wrap')).toBeVisible();
    await expect(split.locator('.flow-node')).toHaveCount(2);
    await expect(split.locator('.source-editor')).toBeVisible();

    if (!checkInvalidYaml) return;
    await split.getByRole('textbox', { name: 'Editor content' }).focus();
    await this.page.keyboard.press('ControlOrMeta+A');
    await this.page.keyboard.insertText('entryStep: [');
    await expect(split.locator('.view-lines')).toContainText('entryStep: [');
    await split.getByRole('button', { name: 'Apply and save' }).click();
    await expect(split.locator('.source-editor .error-panel')).toBeVisible();
    await expect(split.locator('.view-lines')).toContainText('entryStep: [');
    await expect(split.locator('.flow-node')).toHaveCount(2);
  }

  public async openTask(consoleUrl: string, taskId: string): Promise<void> {
    requireIdentifier(taskId, 'task');
    await this.open(consoleUrl, `/tasks/${encodeURIComponent(taskId)}`, 'taskDetails');
  }

  public async openFirstTaskFlowRun(taskId: string): Promise<string> {
    requireIdentifier(taskId, 'task');
    const link = this.page.locator(`a[href^="/tasks/${attributeValue(taskId)}/flowruns/"]`).first();
    await link.waitFor({ state: 'visible' });
    const href = await link.getAttribute('href');
    if (!href) throw new Error(`Task '${taskId}' does not expose a Flow Run link.`);
    const runId = decodeURIComponent(href.split('/').at(-1)!);
    await link.click();
    await this.page.getByTestId(TestIds.flowObservability.taskRunDetails).waitFor({ state: 'visible' });
    return runId;
  }

  public async openFirstFlowRun(): Promise<string> {
    const link = this.page.locator('[data-testid="flow-observability-runs"] a[href^="/flow-runs/"]').first();
    await link.waitFor({ state: 'visible' });
    const href = await link.getAttribute('href');
    if (!href) throw new Error('The Flow Runs list does not expose a details link.');
    const runId = decodeURIComponent(href.split('/').at(-1)!);
    await link.click();
    await this.page.getByTestId(TestIds.flowObservability.runDetails).waitFor({ state: 'visible' });
    return runId;
  }

  public async openFirstAgentRun(): Promise<string> {
    const link = this.page.locator('[data-testid="flow-observability-agent-runs"] a[href^="/runs/"]').first();
    await link.waitFor({ state: 'visible' });
    const href = await link.getAttribute('href');
    if (!href) throw new Error('The Agent Runs list does not expose a details link.');
    const runId = decodeURIComponent(href.split('/').at(-1)!);
    await link.click();
    await this.page.getByTestId(TestIds.flowObservability.agentRunner).waitFor({ state: 'visible' });
    return runId;
  }

  public async createAgentRun(consoleUrl: string, agentName: string, prompt: string): Promise<string> {
    requireIdentifier(agentName, 'agent');
    await this.open(consoleUrl, `/agents/${encodeURIComponent(agentName)}/run`, 'agentRunner');
    await this.page.locator(`[data-testid="${TestIds.flowObservability.agentRunner}"][data-interactive="true"]`).waitFor({ state: 'visible' });
    const promptInput = this.page.getByTestId(TestIds.flowObservability.agentRunPrompt);
    await fillAndCommit(promptInput, prompt);
    await expect(promptInput).toHaveValue(prompt);
    const submit = this.page.getByTestId(TestIds.flowObservability.agentRunSubmit);
    await expect(submit).toBeEnabled();
    await submit.click();
    const details = this.page.getByTestId(TestIds.flowObservability.agentRunDetails);
    await details.waitFor({ state: 'visible' });
    const runId = await details.getAttribute('data-run-id');
    if (!runId) throw new Error(`Agent '${agentName}' did not expose its Run identifier.`);
    return runId;
  }
}

function requireIdentifier(value: string, label: string): void {
  if (!/^[a-zA-Z0-9_.:-]+$/.test(value)) throw new Error(`Unsupported ${label} identifier '${value}'.`);
}

function attributeValue(value: string): string {
  requireIdentifier(value, 'locator');
  return value;
}
