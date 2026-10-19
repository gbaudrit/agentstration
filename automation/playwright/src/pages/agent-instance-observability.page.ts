import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { LoginPage } from './login.page.js';
import { fillAndCommit } from './controls.js';

interface AgentWire {
  generation: number;
}

interface AgentInstanceWire {
  instanceId: string;
  agentName: string;
  agentGeneration: number;
  revisionId: string;
  runKind: string;
  runId: string;
  participantId?: string | null;
  workerId: string;
}

interface FlowRunWire {
  id: string;
  status: string | number;
}

interface LocalAccountWire {
  principalId: string;
}

interface WorkspaceWire {
  id: string;
}

const primaryAgentName = 'worker-observability-agent';
const secondaryAgentName = 'agent-instance-observability-peer';
const modelProfileName = 'worker-observability-reasoning';
const flowName = 'agent-instance-observability-flow';

export class AgentInstanceObservabilityPage {
  public constructor(private readonly page: Page) {}

  public get emptyState(): Locator { return this.page.getByTestId(TestIds.agentInstanceObservability.empty); }
  public get instanceList(): Locator { return this.page.getByTestId(TestIds.agentInstanceObservability.list); }

  public async ensureMultiAgentFixture(consoleUrl: string): Promise<void> {
    await this.ensureAgent(consoleUrl, secondaryAgentName, 'Agent instance observability peer');
    const flowResponse = await this.page.request.get(`${consoleUrl}/api/flows/${flowName}`);
    if (flowResponse.status() === 404) {
      const created = await this.page.request.post(`${consoleUrl}/api/flows`, {
        data: {
          name: flowName,
          displayName: 'Agent instance observability flow',
          description: 'Deterministic concurrent Flow used to observe effective Agent participants.',
          version: '1.0.0',
          enabled: true,
          definition: {
            flowKind: 'orchestration',
            participants: [
              { kind: 0, id: primaryAgentName, namespace: 'default' },
              { kind: 0, id: secondaryAgentName, namespace: 'default' },
            ],
            pattern: { strategy: 'concurrent' },
          },
        },
      });
      expect(created.status(), await created.text()).toBe(201);
      const published = await this.page.request.post(`${consoleUrl}/api/flows/${flowName}/versions`, {
        data: { version: '1.0.0', activate: true },
      });
      expect(published.status(), await published.text()).toBe(201);
    } else {
      expect(flowResponse.status(), await flowResponse.text()).toBe(200);
    }
  }

  public async open(consoleUrl: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/agent-instances`);
    if (!response?.ok()) throw new Error(`Agent instance observability returned HTTP ${response?.status() ?? 'no response'}.`);
    await expect(this.page.getByTestId(TestIds.agentInstanceObservability.page)).toBeVisible();
  }

  public async expectEmpty(): Promise<void> {
    await expect(this.page.getByTestId(TestIds.agentInstanceObservability.empty)).toBeVisible();
    await expect(this.page.getByTestId(TestIds.agentInstanceObservability.row)).toHaveCount(0);
  }

  public async expectInstances(instances: readonly AgentInstanceWire[]): Promise<void> {
    await expect(this.page.getByTestId(TestIds.agentInstanceObservability.list)).toBeVisible();
    await expect(this.page.getByTestId(TestIds.agentInstanceObservability.row)).toHaveCount(instances.length);
    for (const instance of instances) {
      const row = this.instanceRow(instance.instanceId);
      await expect(row).toBeVisible();
      await expect(row).toHaveAttribute('data-agent-name', instance.agentName);
      await expect(row).toHaveAttribute('data-agent-generation', `${instance.agentGeneration}`);
      await expect(row).toHaveAttribute('data-revision-id', instance.revisionId);
      await expect(row).toHaveAttribute('data-run-kind', instance.runKind);
      await expect(row).toHaveAttribute('data-run-id', instance.runId);
      await expect(row).toHaveAttribute('data-worker-id', instance.workerId);
    }
  }

  public async createRuntimeRun(consoleUrl: string, prompt: string): Promise<string> {
    const response = await this.page.goto(`${consoleUrl}/agents/${primaryAgentName}/run`);
    if (!response?.ok()) throw new Error(`Agent runner returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.locator(`[data-testid="${TestIds.flowObservability.agentRunner}"][data-interactive="true"]`).waitFor({ state: 'visible' });
    await fillAndCommit(this.page.getByTestId(TestIds.flowObservability.agentRunPrompt), prompt);
    await this.page.getByTestId(TestIds.flowObservability.agentRunSubmit).click();
    const details = this.page.getByTestId(TestIds.flowObservability.agentRunDetails);
    await details.waitFor({ state: 'visible' });
    const runId = await details.getAttribute('data-run-id');
    if (!runId) throw new Error('The Agent Run did not expose its identifier.');
    return runId;
  }

  public async createFlowRun(consoleUrl: string): Promise<string> {
    const response = await this.page.request.post(`${consoleUrl}/api/flows/${flowName}/runs`, {
      data: { input: { request: 'Execute both deterministic participants for Agent instance observability.' } },
    });
    expect(response.status(), await response.text()).toBe(202);
    return (await response.json() as FlowRunWire).id;
  }

  public async waitForRunInstances(consoleUrl: string, runIds: readonly string[], expectedCount: number): Promise<AgentInstanceWire[]> {
    let matching: AgentInstanceWire[] = [];
    await expect.poll(async () => {
      const instances = await this.getInstances(consoleUrl);
      matching = instances.filter(instance => runIds.includes(instance.runId));
      return matching.length;
    }, { timeout: 30_000 }).toBe(expectedCount);
    return matching;
  }

  public async waitForNoInstances(consoleUrl: string, runIds?: readonly string[]): Promise<void> {
    await expect.poll(async () => {
      const instances = await this.getInstances(consoleUrl);
      return runIds ? instances.filter(instance => runIds.includes(instance.runId)).length : instances.length;
    }, { timeout: 30_000 }).toBe(0);
  }

  public async waitForFlowRun(consoleUrl: string, runId: string, expected: 'Succeeded' | 'Failed'): Promise<void> {
    await expect.poll(async () => {
      const response = await this.page.request.get(`${consoleUrl}/api/flowRuns/${encodeURIComponent(runId)}`);
      expect(response.status(), await response.text()).toBe(200);
      const run = await response.json() as FlowRunWire;
      return flowRunStatusName(run.status);
    }, { timeout: 90_000 }).toBe(expected);
  }

  public async followAgentLink(consoleUrl: string, instance: AgentInstanceWire): Promise<void> {
    await this.instanceRow(instance.instanceId).locator(`a[href="/agents/${instance.agentName}"]`).click();
    await expect(this.page).toHaveURL(`${consoleUrl}/agents/${instance.agentName}`);
  }

  public async followRunLink(consoleUrl: string, instance: AgentInstanceWire): Promise<void> {
    const prefix = instance.runKind === 'FlowRun' ? 'flow-runs' : 'runs';
    await this.instanceRow(instance.instanceId).locator(`a[href="/${prefix}/${instance.runId}"]`).click();
    await expect(this.page).toHaveURL(`${consoleUrl}/${prefix}/${instance.runId}`);
  }

  public async followWorkerLink(consoleUrl: string, instance: AgentInstanceWire): Promise<void> {
    await this.instanceRow(instance.instanceId).locator(`a[href="/settings/runtime-workers/${instance.workerId}"]`).click();
    await expect(this.page).toHaveURL(`${consoleUrl}/settings/runtime-workers/${instance.workerId}`);
    await expect(this.page.getByTestId(TestIds.runtimeWorkerObservability.details)).toBeVisible();
  }

  public async exerciseAuthorizationBoundaries(consoleUrl: string, workerId: string, protectedRunId: string): Promise<void> {
    const workspaceId = await this.page.getByTestId(TestIds.console.shell).getAttribute('data-workspace-id');
    if (!workspaceId) throw new Error('The authenticated Console shell does not expose its Workspace identifier.');
    const suffix = Date.now().toString(36);
    const userName = `agent-instance-viewer-${suffix}`;
    const password = 'Agent-Instance-Viewer-707!';
    const siblingResponse = await this.page.request.post(`${consoleUrl}/api/identity/workspaces`, {
      data: { name: `agent-instance-isolation-${suffix}`, displayName: `Agent instance isolation ${suffix}` },
    });
    expect(siblingResponse.status(), await siblingResponse.text()).toBe(201);
    const sibling = await siblingResponse.json() as WorkspaceWire;
    const accountResponse = await this.page.request.post(`${consoleUrl}/api/identity/accounts`, {
      data: { userName, password, displayName: 'Agent instance observability viewer', email: null, workspaceId, role: 'Viewer' },
    });
    expect(accountResponse.status(), await accountResponse.text()).toBe(201);
    const account = await accountResponse.json() as LocalAccountWire;
    const membership = await this.page.request.put(`${consoleUrl}/api/identity/workspaces/${sibling.id}/memberships/${account.principalId}`, {
      data: { role: 'Viewer' },
    });
    expect(membership.status(), await membership.text()).toBe(200);

    const browser = this.page.context().browser();
    if (!browser) throw new Error('The browser context is unavailable.');
    const readerContext = await browser.newContext({ locale: 'en-US', colorScheme: 'dark' });
    const readerPage = await readerContext.newPage();
    try {
      await new LoginPage(readerPage).signIn(consoleUrl, userName, password);
      const worker = await readerPage.request.get(`${consoleUrl}/api/runtime/workers/${workerId}`);
      expect(worker.status()).toBe(403);
      const selected = await readerPage.request.post(`${consoleUrl}/api/identity/context/workspace`, {
        data: { workspaceId: sibling.id },
      });
      expect(selected.status(), await selected.text()).toBe(200);
      const instancesResponse = await readerPage.request.get(`${consoleUrl}/api/runtime/agent-instances`);
      expect(instancesResponse.status(), await instancesResponse.text()).toBe(200);
      const instances = await instancesResponse.json() as AgentInstanceWire[];
      expect(instances.some(instance => instance.runId === protectedRunId)).toBe(false);
    } finally {
      await readerContext.close();
    }
  }

  public async expectResponsiveLayout(): Promise<void> {
    const surface = this.page.getByTestId(TestIds.agentInstanceObservability.page);
    await expect(surface).toBeVisible();
    const viewportWidth = this.page.viewportSize()?.width ?? 0;
    const documentWidth = await this.page.evaluate(() => document.documentElement.scrollWidth);
    expect(documentWidth).toBeLessThanOrEqual(viewportWidth);
    const scrollContainer = this.instanceList.locator('.data-grid-wrap');
    await expect(scrollContainer).toBeVisible();
    const metrics = await scrollContainer.evaluate(element => ({
      clientWidth: element.clientWidth,
      scrollWidth: element.scrollWidth,
    }));
    expect(metrics.clientWidth).toBeLessThanOrEqual(viewportWidth);
    expect(metrics.scrollWidth).toBeGreaterThan(metrics.clientWidth);
    await scrollContainer.evaluate(element => { element.scrollLeft = element.scrollWidth; });
    expect(await scrollContainer.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
  }

  private instanceRow(instanceId: string): Locator {
    return this.page.locator(`[data-testid="${TestIds.agentInstanceObservability.row}"][data-instance-id="${instanceId}"]`);
  }

  private async getInstances(consoleUrl: string): Promise<AgentInstanceWire[]> {
    const response = await this.page.request.get(`${consoleUrl}/api/runtime/agent-instances`);
    expect(response.status(), await response.text()).toBe(200);
    return await response.json() as AgentInstanceWire[];
  }

  private async ensureAgent(consoleUrl: string, name: string, displayName: string): Promise<void> {
    const response = await this.page.request.get(`${consoleUrl}/api/agents/${name}`);
    let agent: AgentWire;
    if (response.status() === 404) {
      const created = await this.page.request.put(`${consoleUrl}/api/agents/${name}`, {
        headers: { 'If-None-Match': '*' },
        data: {
          apiVersion: 'agentstration.io/v1',
          kind: 'Agent',
          metadata: { name, namespace: 'default' },
          definition: {
            displayName,
            description: 'Deterministic Agent used by the Agent instance browser scenario.',
            instructions: 'Return a concise deterministic response.',
            modelProfile: { name: modelProfileName, namespace: 'default' },
            runtimeProfile: { name: 'maf-builtin', namespace: 'default' },
            tools: [], toolSets: [], behaviors: [], middleware: [], contextProviders: [], settings: {},
          },
        },
      });
      expect(created.status(), await created.text()).toBe(200);
      agent = await created.json() as AgentWire;
    } else {
      expect(response.status(), await response.text()).toBe(200);
      agent = await response.json() as AgentWire;
    }
    const prepared = await this.page.request.post(`${consoleUrl}/api/runtime/agents/${name}/prepare?generation=${agent.generation}`);
    expect(prepared.status(), await prepared.text()).toBe(200);
  }
}

function flowRunStatusName(value: string | number): string {
  if (typeof value === 'string') return value;
  return ['Pending', 'Running', 'WaitingForInput', 'Succeeded', 'Failed', 'Cancelled'][value] ?? `${value}`;
}
