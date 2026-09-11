import { type Page } from '@playwright/test';
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
    await fillAndCommit(this.page.getByTestId(TestIds.flowObservability.agentRunPrompt), prompt);
    const submit = this.page.getByTestId(TestIds.flowObservability.agentRunSubmit);
    await submit.waitFor({ state: 'visible' });
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
