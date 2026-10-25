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

export interface OperationalWorkspaceFixture {
  execution: ExecutionScopeFixture;
  instanceFlowName: string;
}

export interface OperationalRunEvidence {
  flowRunId: string;
  workItemId: string;
  definitionScopeRef: string;
  logicalScopeRef: string;
  executionScopeRef: string;
  operationalWorkspaceId: string;
}

export class OperationalWorkspacesPage {
  public constructor(private readonly page: Page) {}

  public get runDetails(): Locator { return this.page.getByTestId(TestIds.flowObservability.runDetails); }
  public get runs(): Locator { return this.page.getByTestId(TestIds.flowObservability.runs); }
  public get definitionScope(): Locator { return this.page.getByTestId(TestIds.flowObservability.flowRunDefinitionScope); }
  public get executionScope(): Locator { return this.page.getByTestId(TestIds.flowObservability.flowRunExecutionScope); }

  public async ensureFixture(
    _consoleUrl: string,
    execution: ExecutionScopeFixture,
  ): Promise<OperationalWorkspaceFixture> {
    return { execution, instanceFlowName: execution.flowName };
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
      const response = await this.page.request.get(`${consoleUrl}/api/flowRuns/${encodeURIComponent(run.flowRunId)}`);
      expect(response.status(), await response.text()).toBe(404);
    }
  }

  private async selectContext(consoleUrl: string, scopeRef: string): Promise<IdentityContextWire> {
    const selected = await this.page.request.post(`${consoleUrl}/bff/identity/context/selection`, {
      data: { scopeRef },
    });
    expect(selected.status(), await selected.text()).toBe(200);
    const context = await this.read<IdentityContextWire>(`${consoleUrl}/api/identity/context`);
    expect(context.selectedScopeRef).toBe(scopeRef);
    return context;
  }

  private async read<T>(url: string): Promise<T> {
    const response = await this.page.request.get(url);
    const body = await response.text();
    expect(response.status(), body).toBe(200);
    return JSON.parse(body) as T;
  }

  private async open(url: string, marker: string): Promise<void> {
    const response = await this.page.goto(url, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Operational Workspace route '${url}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(marker).waitFor({ state: 'visible' });
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
