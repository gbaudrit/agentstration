import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import type { ExecutionScopeFixture } from './execution-scopes.page.js';

interface IdentityContextWire {
  context: { tenantId: string; workspaceId: string };
  selectedScopeRef: string;
  selectedKind: string | number;
  availableWorkspaces: Array<{ id: string; tenantId: string; name: string }>;
  availableContexts: Array<{
    scopeRef: string;
    kind: string | number;
    tenantId?: string | null;
    workspaceId?: string | null;
  }>;
}

interface FlowRunWire {
  id: string;
  status: string | number;
  definitionScopeRef?: string;
  executionScopeRef: string;
  scope: { logicalScopeRef?: string | null };
  workItemResourceId?: string;
}

interface WorkItemWire {
  id: string;
  status: string | number;
  executionScopeRef: string;
}

interface AgentWire {
  uid: string;
  generation: number;
}

export interface OperationalWorkspaceFixture {
  execution: ExecutionScopeFixture;
  instanceFlowName: string;
  tenantFlowName: string;
}

export interface OperationalRunEvidence {
  flowRunId: string;
  workItemId: string;
  definitionScopeRef: string;
  logicalScopeRef: string;
  executionScopeRef: string;
  operationalWorkspaceId: string;
}

const tenantAgentName = 'tenant-operational-agent';
const tenantFlowName = 'tenant-operational-flow';
const instanceModelProfileName = 'execution-scope-reasoning';

export class OperationalWorkspacesPage {
  private selectedScopeRef: string | undefined;

  public constructor(private readonly page: Page) {}

  public get runDetails(): Locator { return this.page.getByTestId(TestIds.flowObservability.runDetails); }
  public get runs(): Locator { return this.page.getByTestId(TestIds.flowObservability.runs); }
  public get definitionScope(): Locator { return this.page.getByTestId(TestIds.flowObservability.flowRunDefinitionScope); }
  public get executionScope(): Locator { return this.page.getByTestId(TestIds.flowObservability.flowRunExecutionScope); }

  public async ensureFixture(
    consoleUrl: string,
    execution: ExecutionScopeFixture,
  ): Promise<OperationalWorkspaceFixture> {
    const agent = await this.ensureTenantAgent(consoleUrl, execution.scopes.tenant);
    await this.ensureTenantFlow(consoleUrl, execution.scopes.tenant, agent.uid);
    return { execution, instanceFlowName: execution.flowName, tenantFlowName };
  }

  public async assertOperationalTopologyHidden(
    consoleUrl: string,
    fixture: OperationalWorkspaceFixture,
  ): Promise<{ instanceWorkspaceId: string; tenantWorkspaceId: string }> {
    const business = await this.selectContext(consoleUrl, fixture.execution.scopes.workspace);
    const businessWorkspaceIds = business.availableWorkspaces.map(workspace => workspace.id);

    const instance = await this.selectContext(consoleUrl, fixture.execution.scopes.instance);
    const instanceWorkspaceId = instance.context.workspaceId;
    expect(instance.selectedScopeRef).toBe(fixture.execution.scopes.instance);
    expect(businessWorkspaceIds).not.toContain(instanceWorkspaceId);
    expect(instance.availableContexts.some(context => context.workspaceId === instanceWorkspaceId)).toBe(false);

    const tenant = await this.selectContext(consoleUrl, fixture.execution.scopes.tenant);
    const tenantWorkspaceId = tenant.context.workspaceId;
    expect(tenant.selectedScopeRef).toBe(fixture.execution.scopes.tenant);
    expect(businessWorkspaceIds).not.toContain(tenantWorkspaceId);
    expect(tenant.availableContexts.some(context => context.workspaceId === tenantWorkspaceId)).toBe(false);
    expect(tenantWorkspaceId).not.toBe(instanceWorkspaceId);

    return { instanceWorkspaceId, tenantWorkspaceId };
  }

  public async executeFlow(
    consoleUrl: string,
    flowName: string,
    definitionScopeRef: string,
    logicalScopeRef: string,
  ): Promise<OperationalRunEvidence> {
    const context = await this.selectContext(consoleUrl, logicalScopeRef);
    const operationalWorkspaceId = context.context.workspaceId;
    const executionScopeRef = `/workspaces/${operationalWorkspaceId}`;

    await this.open(
      `${consoleUrl}/flows/${encodeURIComponent(flowName)}?scopeRef=${encodeURIComponent(definitionScopeRef)}`,
      TestIds.flowObservability.flowDetails,
    );
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-context-scope', logicalScopeRef);
    await expect(async () => {
      await this.page.getByTestId(TestIds.flowObservability.flowRunsTab).click();
      await expect(this.page.getByTestId(TestIds.flowObservability.flowRunOpen)).toBeVisible();
    }).toPass({ timeout: 30_000 });
    await this.page.getByTestId(TestIds.flowObservability.flowRunOpen).click();
    const input = this.page.getByRole('textbox', { name: 'JSON input' });
    await input.fill('{}');
    await input.dispatchEvent('change');
    await this.page.getByTestId(TestIds.flowObservability.flowRunSubmit).click();
    await this.page.waitForURL(/\/flow-runs\/[^?]+\?scopeRef=/, { timeout: 30_000 });
    await this.runDetails.waitFor({ state: 'visible' });

    const flowRunId = decodeURIComponent(new URL(this.page.url()).pathname.split('/').at(-1) ?? '');
    if (!flowRunId) throw new Error('The operational Flow runner did not expose a Flow Run identifier.');
    const flowUrl = `${consoleUrl}/api/flowRuns/${encodeURIComponent(flowRunId)}?scopeRef=${encodeURIComponent(executionScopeRef)}`;
    await expect.poll(async () => flowStatus(await this.read<FlowRunWire>(flowUrl)), { timeout: 90_000 })
      .toBe('Succeeded');
    const flow = await this.read<FlowRunWire>(flowUrl);
    expect(flow.definitionScopeRef).toBe(definitionScopeRef);
    expect(flow.scope.logicalScopeRef).toBe(logicalScopeRef);
    expect(flow.executionScopeRef).toBe(executionScopeRef);
    if (!flow.workItemResourceId) throw new Error(`Flow Run '${flow.id}' did not expose its WorkItem.`);

    const workUrl = `${consoleUrl}/api/work/workitems/${encodeURIComponent(flow.workItemResourceId)}?scopeRef=${encodeURIComponent(executionScopeRef)}`;
    await expect.poll(async () => workStatus(await this.read<WorkItemWire>(workUrl)), { timeout: 90_000 })
      .toBe('Completed');
    const work = await this.read<WorkItemWire>(workUrl);
    expect(work.executionScopeRef).toBe(executionScopeRef);
    await expect(this.definitionScope).toContainText(definitionScopeRef);
    await expect(this.executionScope).toContainText(executionScopeRef);

    return {
      flowRunId,
      workItemId: work.id,
      definitionScopeRef,
      logicalScopeRef,
      executionScopeRef,
      operationalWorkspaceId,
    };
  }

  public async recoverThroughAdministrativeContext(
    consoleUrl: string,
    evidence: OperationalRunEvidence,
  ): Promise<void> {
    await this.open(`${consoleUrl}/agents`, TestIds.resourceAdministration.agents);
    await this.selectContext(consoleUrl, evidence.logicalScopeRef);
    await this.open(`${consoleUrl}/flow-runs`, TestIds.flowObservability.runs);
    const link = this.page.locator(`a[href^="/flow-runs/${evidence.flowRunId}?"]`);
    await expect(link).toBeVisible();
    await link.click();
    await this.runDetails.waitFor({ state: 'visible' });
    await expect(this.definitionScope).toContainText(evidence.definitionScopeRef);
    await expect(this.executionScope).toContainText(evidence.executionScopeRef);
  }

  public async expectRunsHiddenFromContext(
    consoleUrl: string,
    scopeRef: string,
    evidence: readonly OperationalRunEvidence[],
  ): Promise<void> {
    await this.selectContext(consoleUrl, scopeRef);
    await this.open(`${consoleUrl}/flow-runs`, TestIds.flowObservability.runs);
    for (const run of evidence) {
      await expect(this.page.locator(`a[href^="/flow-runs/${run.flowRunId}?"]`)).toHaveCount(0);
      const response = await this.page.request.get(
        `${consoleUrl}/api/flowRuns/${encodeURIComponent(run.flowRunId)}`,
        { headers: this.contextHeaders() },
      );
      expect(response.status(), await response.text()).toBe(404);
    }
  }

  private async ensureTenantAgent(consoleUrl: string, tenantScopeRef: string): Promise<AgentWire> {
    const headers = { 'X-Agentstration-Context': tenantScopeRef };
    const url = `${consoleUrl}/api/agents/${tenantAgentName}?scopeRef=${encodeURIComponent(tenantScopeRef)}`;
    const current = await this.page.request.get(url, { headers });
    let agent: AgentWire;
    if (current.status() === 404) {
      const created = await this.page.request.put(`${consoleUrl}/api/agents/${tenantAgentName}`, {
        headers: { ...headers, 'If-None-Match': '*' },
        data: {
          apiVersion: 'agentstration.io/v1',
          kind: 'Agent',
          scopeRef: tenantScopeRef,
          metadata: { name: tenantAgentName, namespace: 'default' },
          definition: {
            displayName: 'Tenant operational Agent',
            description: 'Deterministic Tenant Agent for operational Workspace browser coverage.',
            instructions: 'Return a concise deterministic response.',
            modelProfile: { name: instanceModelProfileName, namespace: 'default', scopeRef: '/instance' },
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
      expect(current.status(), await current.text()).toBe(200);
      agent = await current.json() as AgentWire;
    }

    const revisionName = `${tenantAgentName}--${agent.generation.toString().padStart(6, '0')}`;
    let revision = await this.page.request.get(
      `${consoleUrl}/api/agents/${tenantAgentName}/revisions/${revisionName}?scopeRef=${encodeURIComponent(tenantScopeRef)}`,
      { headers },
    );
    if (revision.status() === 404) {
      const prepared = await this.page.request.post(
        `${consoleUrl}/api/runtime/agents/${tenantAgentName}/prepare?generation=${agent.generation}&scopeRef=${encodeURIComponent(tenantScopeRef)}`,
        { headers },
      );
      expect(prepared.status(), await prepared.text()).toBe(200);
      revision = await this.page.request.get(
        `${consoleUrl}/api/agents/${tenantAgentName}/revisions/${revisionName}?scopeRef=${encodeURIComponent(tenantScopeRef)}`,
        { headers },
      );
    }
    expect(revision.status(), await revision.text()).toBe(200);
    return agent;
  }

  private async ensureTenantFlow(consoleUrl: string, tenantScopeRef: string, agentUid: string): Promise<void> {
    const headers = { 'X-Agentstration-Context': tenantScopeRef };
    const url = `${consoleUrl}/api/flows/${tenantFlowName}?scopeRef=${encodeURIComponent(tenantScopeRef)}`;
    const current = await this.page.request.get(url, { headers });
    if (current.status() === 200) return;
    expect(current.status(), await current.text()).toBe(404);

    const created = await this.page.request.post(`${consoleUrl}/api/flows`, {
      headers,
      data: {
        name: tenantFlowName,
        displayName: 'Tenant operational Flow',
        description: 'Deterministic Tenant Flow for operational Workspace browser coverage.',
        version: '1.0.0',
        enabled: true,
        scopeRef: tenantScopeRef,
        definition: {
          flowKind: 'direct',
          target: {
            kind: 0,
            id: tenantAgentName,
            namespace: 'default',
            scopeRef: tenantScopeRef,
            resourceUid: agentUid,
          },
        },
      },
    });
    expect(created.status(), await created.text()).toBe(201);
    const published = await this.page.request.post(
      `${consoleUrl}/api/flows/${tenantFlowName}/versions?scopeRef=${encodeURIComponent(tenantScopeRef)}`,
      { headers, data: { version: '1.0.0', activate: true } },
    );
    expect(published.status(), await published.text()).toBe(201);
  }

  private async selectContext(consoleUrl: string, scopeRef: string): Promise<IdentityContextWire> {
    this.selectedScopeRef = scopeRef;
    const context = await this.read<IdentityContextWire>(`${consoleUrl}/api/identity/context`);
    expect(context.selectedScopeRef).toBe(scopeRef);
    return context;
  }

  private async read<T>(url: string): Promise<T> {
    const response = await this.page.request.get(url, { headers: this.contextHeaders() });
    const body = await response.text();
    expect(response.status(), body).toBe(200);
    return JSON.parse(body) as T;
  }

  private async open(url: string, marker: string): Promise<void> {
    const contextualUrl = new URL(url);
    if (this.selectedScopeRef) contextualUrl.searchParams.set('context', this.selectedScopeRef);
    const response = await this.page.goto(contextualUrl.toString(), { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Operational Workspace route '${url}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(marker).waitFor({ state: 'visible' });
  }

  private contextHeaders(): Record<string, string> {
    return this.selectedScopeRef ? { 'X-Agentstration-Context': this.selectedScopeRef } : {};
  }
}

function flowStatus(run: FlowRunWire): string {
  return typeof run.status === 'string'
    ? run.status
    : ['Pending', 'Running', 'WaitingForInput', 'Succeeded', 'Failed', 'Cancelled', 'TimedOut', 'WaitingForChild'][run.status] ?? `${run.status}`;
}

function workStatus(work: WorkItemWire): string {
  return typeof work.status === 'string'
    ? work.status
    : ['Created', 'Queued', 'Running', 'WaitingForInput', 'WaitingForApproval', 'Paused', 'Completed', 'Failed', 'Cancelled'][work.status] ?? `${work.status}`;
}
