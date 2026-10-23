import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { LoginPage } from './login.page.js';
import { fillAndCommit } from './controls.js';

interface RuntimeRunWire {
  id: string;
  status: { state: string; errorCode?: string | null };
}

interface FlowRunWire {
  id: string;
  status: string | number;
  error?: { code?: string | null } | null;
}

interface WorkerSummaryWire {
  workerId: string;
  presence: string | number;
  sessionId?: string | null;
}

interface LocalAccountWire {
  principalId: string;
}

interface WorkspaceWire {
  id: string;
}

interface AgentWire {
  generation: number;
}

interface ScopedResourceWire {
  uid: string;
  namespace: string;
  kind: string;
  generation: number;
}

interface ResourceAccessGrantWire {
  targetScopeRef: string;
  actions: string[];
  includeDescendants: boolean;
}

interface ResourceAccessPolicyWire {
  resource: {
    uid: string;
    ownerScopeRef: string;
    namespace: string;
    kind: string;
    generation: number;
  };
  grants: ResourceAccessGrantWire[];
}

const observabilityAgentName = 'worker-observability-agent';
const observabilityFlowName = 'worker-observability-flow';
const observabilityProviderName = 'worker-observability-ollama';
const observabilityProfileName = 'worker-observability-reasoning';

export class RuntimeWorkerObservabilityPage {
  public constructor(private readonly page: Page) {}

  public async openInventory(consoleUrl: string): Promise<void> {
    await this.open(`${consoleUrl}/settings/runtime-workers`, TestIds.runtimeWorkerObservability.inventory);
  }

  public workerRow(workerId: string): Locator {
    return this.page.getByTestId(TestIds.runtimeWorkerObservability.workerRow)
      .filter({ has: this.page.locator(`a[href="/settings/runtime-workers/${workerId}"]`) });
  }

  public async expectPresence(workerId: string, presence: 'Online' | 'Stale' | 'Offline' | 'Unknown'): Promise<string | null> {
    const row = this.workerRow(workerId);
    await expect(row).toBeVisible();
    await expect(row).toHaveAttribute('data-presence', presence);
    return await row.getAttribute('data-session-id');
  }

  public async waitForPresence(consoleUrl: string, workerId: string, presence: 'Online' | 'Stale' | 'Offline' | 'Unknown'): Promise<void> {
    await expect.poll(async () => {
      const response = await this.page.request.get(`${consoleUrl}/api/runtime/workers`);
      expect(response.status(), await response.text()).toBe(200);
      const workers = await response.json() as WorkerSummaryWire[];
      return workerPresenceName(workers.find(worker => sameIdentifier(worker.workerId, workerId))?.presence);
    }, { timeout: 30_000 }).toBe(presence);
    await this.openInventory(consoleUrl);
    await this.expectPresence(workerId, presence);
  }

  public async openWorker(consoleUrl: string, workerId: string): Promise<void> {
    await this.open(`${consoleUrl}/settings/runtime-workers/${workerId}`, TestIds.runtimeWorkerObservability.details);
  }

  public async openAssignments(): Promise<void> {
    const tab = this.page.getByTestId(TestIds.runtimeWorkerObservability.assignmentsTab);
    await this.activateTab(tab);
    await expect(this.page.getByTestId(TestIds.runtimeWorkerObservability.assignments)).toBeVisible();
  }

  public async expectAssignment(runId: string, state: 'Succeeded' | 'Failed'): Promise<void> {
    const row = this.assignmentRow(runId);
    await expect(row).toBeVisible();
    await expect(row).toHaveAttribute('data-assignment-state', state);
  }

  public async openAssignment(runId: string): Promise<void> {
    const row = this.assignmentRow(runId);
    await expect(row).toBeVisible();
    const assignmentId = await row.getAttribute('data-assignment-id');
    if (!assignmentId) throw new Error(`Worker assignment '${runId}' does not expose its assignment identifier.`);
    await row.locator(`a[href^="/runs/${runId}"], a[href^="/flow-runs/${runId}"]`).click();
    await expect(this.page.getByTestId(TestIds.runtimeWorkerObservability.placement)).toHaveAttribute('data-assignment-id', assignmentId);
  }

  public async expectCurrentPlacement(workerId: string, expectedAttemptState = 'Succeeded', expectedErrorCode?: string, runId?: string): Promise<void> {
    const placement = this.page.getByTestId(TestIds.runtimeWorkerObservability.placement);
    await expect(placement).toBeVisible();
    await expect(placement).toHaveAttribute('data-assignment-id', /[0-9a-f-]{36}/i);
    if (runId !== undefined) await expect(placement).toHaveAttribute('data-run-id', runId);
    const worker = placement.getByTestId(TestIds.runtimeWorkerObservability.placementWorker);
    await expect(worker).toHaveAttribute('data-worker-id', workerId);
    const attempt = placement.getByTestId(TestIds.runtimeWorkerObservability.placementAttempt).first();
    await expect(attempt).toHaveAttribute('data-worker-id', workerId);
    await expect(attempt).toHaveAttribute('data-attempt-state', expectedAttemptState);
    if (expectedErrorCode !== undefined) await expect(attempt).toHaveAttribute('data-error-code', expectedErrorCode);
    await expect(attempt).toHaveAttribute('data-session-id', /[0-9a-f-]{36}/i);
    await expect(attempt).toHaveAttribute('data-fencing-generation', /^[1-9][0-9]*$/);
    await expect(attempt).toHaveAttribute('data-lease-expires-at', /^\d{4}-\d{2}-\d{2}T/);
  }

  public async openRuntimeRunPlacement(consoleUrl: string, runId: string, workerId: string, state = 'Succeeded', errorCode?: string): Promise<void> {
    await this.open(`${consoleUrl}/runs/${encodeURIComponent(runId)}`, TestIds.flowObservability.agentRunner);
    await this.expectCurrentPlacement(workerId, state, errorCode, runId);
  }

  public async openFlowRunPlacement(consoleUrl: string, runId: string, workerId: string): Promise<void> {
    await this.open(`${consoleUrl}/flow-runs/${encodeURIComponent(runId)}`, TestIds.flowObservability.runDetails);
    const workerTab = this.page.getByTestId(TestIds.runtimeWorkerObservability.flowRunWorkerTab);
    await this.activateTab(workerTab);
    await this.expectCurrentPlacement(workerId, 'Succeeded', undefined, runId);
  }

  public async createSuccessfulRuntimeRun(consoleUrl: string): Promise<string> {
    const runId = await this.createRuntimeRun(consoleUrl, 'Return a deterministic successful Worker observability response.');
    await this.waitForRuntimeRun(consoleUrl, runId, 'Succeeded');
    return runId;
  }

  public async createRuntimeRun(consoleUrl: string, prompt: string): Promise<string> {
    return await this.pageRunner.createAgentRun(consoleUrl, observabilityAgentName, prompt);
  }

  public async createSuccessfulFlowRun(consoleUrl: string): Promise<string> {
    const response = await this.page.request.post(`${consoleUrl}/api/flows/${observabilityFlowName}/runs`, {
      data: { input: { request: 'Return a deterministic successful Flow Run for Worker observability.' } },
    });
    expect(response.status(), await response.text()).toBe(202);
    const run = await response.json() as FlowRunWire;
    await expect.poll(async () => {
      const current = await this.getFlowRun(consoleUrl, run.id);
      return isFlowRunTerminal(current.status) && isFlowRunSucceeded(current.status);
    }, { timeout: 90_000 }).toBe(true);
    return run.id;
  }

  public async ensureExecutionFixture(consoleUrl: string): Promise<void> {
    const workspaceId = await this.page.getByTestId(TestIds.console.shell).getAttribute('data-workspace-id');
    if (!workspaceId) throw new Error('The authenticated Console shell does not expose its Workspace identifier.');
    const workspaceScopeRef = `/workspaces/${workspaceId}`;
    await this.ensureModelProfile(consoleUrl, workspaceScopeRef);
    await this.ensureRuntimeProfileAccess(consoleUrl, workspaceScopeRef);
    const agentResponse = await this.page.request.get(`${consoleUrl}/api/agents/${observabilityAgentName}`);
    let agent: AgentWire;
    if (agentResponse.status() === 404) {
      const created = await this.page.request.put(`${consoleUrl}/api/agents/${observabilityAgentName}`, {
        headers: { 'If-None-Match': '*' },
        data: {
          apiVersion: 'agentstration.io/v1',
          kind: 'Agent',
          metadata: { name: observabilityAgentName, namespace: 'default' },
          definition: {
            displayName: 'Worker observability agent',
            description: 'Deterministic Agent used by the Runtime Worker browser scenario.',
            instructions: 'Return a concise deterministic response.',
            modelProfile: { name: observabilityProfileName, namespace: 'default', scopeRef: workspaceScopeRef },
            runtimeProfile: { name: 'maf-builtin', namespace: 'default', scopeRef: '/instance' },
            tools: [],
            toolSets: [],
            behaviors: [],
            middleware: [],
            contextProviders: [],
            settings: {},
          },
        },
      });
      expect(created.status(), await created.text()).toBe(200);
      agent = await created.json() as AgentWire;
    } else {
      expect(agentResponse.status(), await agentResponse.text()).toBe(200);
      agent = await agentResponse.json() as AgentWire;
    }

    const prepared = await this.page.request.post(
      `${consoleUrl}/api/runtime/agents/${observabilityAgentName}/prepare?generation=${agent.generation}`,
    );
    expect(prepared.status(), await prepared.text()).toBe(200);

    const flowResponse = await this.page.request.get(`${consoleUrl}/api/flows/${observabilityFlowName}`);
    if (flowResponse.status() === 404) {
      const created = await this.page.request.post(`${consoleUrl}/api/flows`, {
        data: {
          name: observabilityFlowName,
          displayName: 'Worker observability flow',
          description: 'Deterministic direct Flow used by the Runtime Worker browser scenario.',
          version: '1.0.0',
          enabled: true,
          definition: {
            flowKind: 'direct',
            target: { kind: 0, id: observabilityAgentName, namespace: 'default' },
          },
        },
      });
      expect(created.status(), await created.text()).toBe(201);
      const published = await this.page.request.post(`${consoleUrl}/api/flows/${observabilityFlowName}/versions`, {
        data: { version: '1.0.0', activate: true },
      });
      expect(published.status(), await published.text()).toBe(201);
    } else {
      expect(flowResponse.status(), await flowResponse.text()).toBe(200);
    }
  }

  public async waitForRuntimeRun(consoleUrl: string, runId: string, state: 'Succeeded' | 'Failed', errorCode?: string): Promise<RuntimeRunWire> {
    let current: RuntimeRunWire | undefined;
    await expect.poll(async () => {
      const response = await this.page.request.get(`${consoleUrl}/api/runtime/runs/${encodeURIComponent(runId)}`);
      expect(response.status(), await response.text()).toBe(200);
      current = await response.json() as RuntimeRunWire;
      return current.status.state;
    }, { timeout: 90_000 }).toBe(state);
    expect(current).toBeDefined();
    if (errorCode !== undefined) expect(current!.status.errorCode).toBe(errorCode);
    return current!;
  }

  public async exerciseAuthorizationBoundaries(consoleUrl: string, protectedRunId: string): Promise<void> {
    const workspaceId = await this.page.getByTestId(TestIds.console.shell).getAttribute('data-workspace-id');
    if (!workspaceId) throw new Error('The authenticated Console shell does not expose its Workspace identifier.');
    const suffix = Date.now().toString(36);
    const userName = `worker-viewer-${suffix}`;
    const password = 'Worker-Viewer-688!';
    const siblingResponse = await this.page.request.post(`${consoleUrl}/api/identity/workspaces`, {
      data: { name: `worker-isolation-${suffix}`, displayName: `Worker isolation ${suffix}` },
    });
    expect(siblingResponse.status(), await siblingResponse.text()).toBe(201);
    const sibling = await siblingResponse.json() as WorkspaceWire;
    const accountResponse = await this.page.request.post(`${consoleUrl}/api/identity/accounts`, {
      data: { userName, password, displayName: 'Worker observability viewer', email: null, workspaceId, role: 'Viewer' },
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
      const workers = await readerPage.request.get(`${consoleUrl}/api/runtime/workers`);
      expect(workers.status()).toBe(403);
      const selected = await readerPage.request.post(`${consoleUrl}/api/identity/context/workspace`, {
        data: { workspaceId: sibling.id },
      });
      expect(selected.status(), await selected.text()).toBe(200);
      const crossWorkspace = await readerPage.request.get(
        `${consoleUrl}/api/runtime/placements/RuntimeRun/${encodeURIComponent(protectedRunId)}`,
      );
      expect(crossWorkspace.status()).toBe(404);
    } finally {
      await readerContext.close();
    }
  }

  public async expectResponsiveLayout(): Promise<void> {
    const inventory = this.page.getByTestId(TestIds.runtimeWorkerObservability.inventory);
    await expect(inventory).toBeVisible();
    const viewportWidth = this.page.viewportSize()?.width ?? 0;
    const documentWidth = await this.page.evaluate(() => document.documentElement.scrollWidth);
    expect(documentWidth).toBeLessThanOrEqual(viewportWidth);
    const row = this.page.getByTestId(TestIds.runtimeWorkerObservability.workerRow).first();
    await expect(row).toBeVisible();
    const box = await row.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.width).toBeLessThanOrEqual(viewportWidth);
  }

  private get pageRunner() {
    return {
      createAgentRun: async (consoleUrl: string, agentName: string, prompt: string): Promise<string> => {
        await this.open(`${consoleUrl}/agents/${encodeURIComponent(agentName)}/run`, TestIds.flowObservability.agentRunner);
        await this.page.locator(`[data-testid="${TestIds.flowObservability.agentRunner}"][data-interactive="true"]`).waitFor({ state: 'visible' });
        const promptInput = this.page.getByTestId(TestIds.flowObservability.agentRunPrompt);
        await fillAndCommit(promptInput, prompt);
        await this.page.getByTestId(TestIds.flowObservability.agentRunSubmit).click();
        const details = this.page.getByTestId(TestIds.flowObservability.agentRunDetails);
        await details.waitFor({ state: 'visible' });
        const runId = await details.getAttribute('data-run-id');
        if (!runId) throw new Error(`Agent '${agentName}' did not expose its Run identifier.`);
        return runId;
      },
    };
  }

  private assignmentRow(runId: string): Locator {
    return this.page.getByTestId(TestIds.runtimeWorkerObservability.assignmentRow)
      .filter({ has: this.page.locator(`a[href^="/runs/${runId}"], a[href^="/flow-runs/${runId}"]`) });
  }

  private async getFlowRun(consoleUrl: string, runId: string): Promise<FlowRunWire> {
    const response = await this.page.request.get(`${consoleUrl}/api/flowRuns/${encodeURIComponent(runId)}`);
    expect(response.status(), await response.text()).toBe(200);
    return await response.json() as FlowRunWire;
  }

  private async ensureModelProfile(consoleUrl: string, workspaceScopeRef: string): Promise<void> {
    const profile = await this.page.request.get(`${consoleUrl}/api/modelprofiles/${observabilityProfileName}`);
    if (profile.status() === 200) return;
    expect(profile.status(), await profile.text()).toBe(404);

    const provider = await this.page.request.get(`${consoleUrl}/api/modelproviders/${observabilityProviderName}`);
    let providerResource: ScopedResourceWire;
    if (provider.status() === 404) {
      const created = await this.page.request.post(`${consoleUrl}/api/modelproviders`, {
        data: {
          name: observabilityProviderName,
          namespace: 'default',
          properties: {
            displayName: 'Worker observability Ollama',
            extension: { name: 'ollama-extension', namespace: 'default' },
            contributionId: 'ollama',
            valueBindings: [],
            specificationOverrides: {},
          },
        },
      });
      expect(created.status(), await created.text()).toBe(201);
      providerResource = await created.json() as ScopedResourceWire;
    } else {
      expect(provider.status(), await provider.text()).toBe(200);
      providerResource = await provider.json() as ScopedResourceWire;
    }
    const policy = await this.page.request.put(`${consoleUrl}/api/resource-access-policies/${providerResource.uid}`, {
      headers: { 'X-Agentstration-Context': '/instance' },
      data: {
        resource: {
          uid: providerResource.uid,
          ownerScopeRef: '/instance',
          namespace: providerResource.namespace,
          kind: providerResource.kind,
          generation: providerResource.generation,
        },
        grants: [{ targetScopeRef: workspaceScopeRef, actions: ['discover', 'use'], includeDescendants: false }],
      },
    });
    expect(policy.status(), await policy.text()).toBe(200);
    const refreshed = await this.page.request.post(
      `${consoleUrl}/api/modelproviders/${observabilityProviderName}/models/refresh`,
    );
    expect(refreshed.status(), await refreshed.text()).toBe(200);
    const created = await this.page.request.post(`${consoleUrl}/api/modelprofiles`, {
      data: {
        name: observabilityProfileName,
        namespace: 'default',
        scopeRef: workspaceScopeRef,
        properties: {
          displayName: 'Worker observability reasoning',
          description: 'Deterministic model profile used by the Runtime Worker browser scenario.',
          provider: { name: observabilityProviderName, namespace: 'default', scopeRef: '/instance' },
          model: { name: 'qwen3:1.7b' },
          generation: { temperature: 0 },
          reasoning: { mode: 0 },
          output: { format: 0, strict: false },
          providerOptions: {},
          secretBindings: [],
        },
      },
    });
    expect(created.status(), await created.text()).toBe(201);
  }

  private async ensureRuntimeProfileAccess(consoleUrl: string, workspaceScopeRef: string): Promise<void> {
    const response = await this.page.request.get(
      `${consoleUrl}/api/runtimeprofiles/maf-builtin?resourceNamespace=default`,
    );
    expect(response.status(), await response.text()).toBe(200);
    const runtimeProfile = await response.json() as ScopedResourceWire;
    await this.ensureUseGrant(consoleUrl, runtimeProfile, '/instance', workspaceScopeRef);
  }

  private async ensureUseGrant(
    consoleUrl: string,
    resource: ScopedResourceWire,
    ownerScopeRef: string,
    targetScopeRef: string,
  ): Promise<void> {
    const policyUrl = `${consoleUrl}/api/resource-access-policies/${resource.uid}`;
    const contextHeaders = { 'X-Agentstration-Context': ownerScopeRef };
    const current = await this.page.request.get(policyUrl, { headers: contextHeaders });
    let policy: ResourceAccessPolicyWire;
    let headers: Record<string, string> | undefined;
    if (current.status() === 404) {
      policy = {
        resource: {
          uid: resource.uid,
          ownerScopeRef,
          namespace: resource.namespace,
          kind: resource.kind,
          generation: resource.generation,
        },
        grants: [],
      };
    } else {
      expect(current.status(), await current.text()).toBe(200);
      policy = await current.json() as ResourceAccessPolicyWire;
      const etag = current.headers()['etag'];
      if (etag) headers = { 'If-Match': etag };
    }
    const existing = policy.grants.find(grant => grant.targetScopeRef === targetScopeRef);
    if (existing?.actions.includes('discover') && existing.actions.includes('use')) return;
    const grants = policy.grants.filter(grant => grant.targetScopeRef !== targetScopeRef);
    grants.push({ targetScopeRef, actions: ['discover', 'use'], includeDescendants: false });
    const saved = await this.page.request.put(policyUrl, {
      headers: { ...contextHeaders, ...headers },
      data: { ...policy, grants },
    });
    expect(saved.status(), await saved.text()).toBe(200);
  }

  private async open(url: string, marker: string): Promise<void> {
    const response = await this.page.goto(url, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Runtime Worker observability route '${url}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(marker).waitFor({ state: 'visible' });
  }

  private async activateTab(tab: Locator): Promise<void> {
    await expect.poll(async () => {
      await tab.click();
      return await tab.getAttribute('aria-selected');
    }).toBe('true');
  }
}

function sameIdentifier(left: string, right: string): boolean {
  return left.replaceAll('-', '').toLocaleLowerCase() === right.replaceAll('-', '').toLocaleLowerCase();
}

function workerPresenceName(presence: string | number | undefined): string | undefined {
  if (typeof presence === 'string' || presence === undefined) return presence;
  return ['Online', 'Stale', 'Offline', 'Unknown'][presence];
}

function isFlowRunTerminal(status: string | number): boolean {
  return typeof status === 'number'
    ? [3, 4, 5, 6].includes(status)
    : ['Succeeded', 'Failed', 'Cancelled', 'TimedOut'].includes(status);
}

function isFlowRunSucceeded(status: string | number | undefined): boolean {
  return status === 3 || status === 'Succeeded';
}
