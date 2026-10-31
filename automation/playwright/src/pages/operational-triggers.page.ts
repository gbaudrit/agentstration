import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit } from './controls.js';
import type { ExecutionScopeFixture } from './execution-scopes.page.js';

interface IdentityContextWire {
  context: { workspaceId: string };
  selectedScopeRef: string;
}

interface TriggerWire {
  uid: string;
  scopeRef: string;
  definition: {
    enabled: boolean;
    target: { flow: { name: string; scopeRef: string } };
    executionScope?: {
      workspaceId: string;
      logicalScopeRef?: string | null;
    } | null;
  };
}

interface TriggerOccurrenceWire {
  id: string;
  workspaceId: string;
  triggerUid: string;
  outcome: string | number;
  workItemId?: string | null;
}

interface WorkItemWire {
  id: string;
  status: string | number;
  executionScopeRef: string;
  [key: string]: unknown;
}

interface FlowRunWire {
  id: string;
  status: string | number;
  executionScopeRef: string;
  definitionScopeRef?: string | null;
  workItemResourceId?: string | null;
}

interface FlowRunPageWire {
  value: FlowRunWire[];
}

interface ResourceIdentityWire {
  uid: string;
  namespace: string;
  kind: string;
  version?: string | null;
}

interface AccessGrantWire {
  targetScopeRef: string;
  actions: string[];
  includeDescendants: boolean;
}

interface AccessPolicyWire {
  grants: AccessGrantWire[];
}

export interface OperationalTriggerFixture {
  execution: ExecutionScopeFixture;
  triggerName: string;
  triggerDisplayName: string;
}

export interface OperationalTriggerEvidence {
  triggerUid: string;
  occurrenceId: string;
  operationalWorkspaceId: string;
  workItemId: string;
  flowRunId: string;
}

const triggerName = 'instance-flow-schedule';
const triggerDisplayName = 'Instance Flow schedule';

export class OperationalTriggersPage {
  private selectedScopeRef: string | undefined;

  public constructor(private readonly page: Page) {}

  public get editor(): Locator { return this.page.getByTestId(TestIds.resourceAdministration.triggerEditor); }
  public get details(): Locator { return this.page.getByTestId(TestIds.resourceAdministration.triggerDetails); }
  public get list(): Locator { return this.page.getByTestId(TestIds.resourceAdministration.triggers); }
  public get occurrences(): Locator { return this.page.getByTestId(TestIds.triggers.occurrences); }

  public fixture(execution: ExecutionScopeFixture): OperationalTriggerFixture {
    return { execution, triggerName, triggerDisplayName };
  }

  public async openCreate(consoleUrl: string, fixture: OperationalTriggerFixture): Promise<void> {
    await this.selectContext(consoleUrl, fixture.execution.scopes.instance);
    await this.open(`${consoleUrl}/triggers/new`, TestIds.resourceAdministration.triggerEditor);
    await expect(this.page.getByTestId(TestIds.triggers.flow).locator('option', {
      hasText: fixture.execution.flowName,
    })).toHaveCount(1);
  }

  public async createDisabled(consoleUrl: string, fixture: OperationalTriggerFixture): Promise<TriggerWire> {
    const name = this.page.getByTestId(TestIds.triggers.name);
    const displayName = this.page.getByTestId(TestIds.triggers.displayName);
    await expect(async () => {
      await fillAndCommit(name, fixture.triggerName);
      await fillAndCommit(displayName, fixture.triggerDisplayName);
      await expect(name).toHaveValue(fixture.triggerName);
      await expect(displayName).toHaveValue(fixture.triggerDisplayName);
    }).toPass();
    await this.page.getByTestId(TestIds.triggers.flow).selectOption(
      `${fixture.execution.scopes.instance}|default|${fixture.execution.flowName}`,
    );
    await Promise.all([
      this.page.waitForURL(new RegExp(`/triggers/${fixture.triggerName}(?:\\?.*)?$`), { timeout: 30_000 }),
      this.page.getByTestId(TestIds.triggers.create).click(),
    ]);
    await this.details.waitFor({ state: 'visible' });
    await expect(this.details).toContainText(fixture.triggerDisplayName);
    await expect(this.page.getByTestId(TestIds.triggers.definitionScope)
      .locator('.resource-scope-badge[title="/instance"]')).toBeVisible();

    await this.open(
      `${consoleUrl}/triggers/${encodeURIComponent(fixture.triggerName)}/edit`,
      TestIds.resourceAdministration.triggerEditor,
    );
    await expect(this.page.getByTestId(TestIds.triggers.name)).toHaveValue(fixture.triggerName);
    await expect(this.page.getByTestId(TestIds.triggers.displayName)).toHaveValue(fixture.triggerDisplayName);
    await expect(this.page.getByTestId(TestIds.triggers.flow)).toHaveValue(
      `${fixture.execution.scopes.instance}|default|${fixture.execution.flowName}`,
    );
    await this.open(
      `${consoleUrl}/triggers/${encodeURIComponent(fixture.triggerName)}`,
      TestIds.resourceAdministration.triggerDetails,
    );

    return await this.readTrigger(consoleUrl, fixture);
  }

  public async assertOperationalOwnership(
    consoleUrl: string,
    fixture: OperationalTriggerFixture,
    trigger: TriggerWire,
  ): Promise<string> {
    const identity = await this.read<IdentityContextWire>(`${consoleUrl}/api/identity/context`);
    const operationalScopeRef = `/workspaces/${identity.context.workspaceId}`;
    expect(identity.selectedScopeRef).toBe(fixture.execution.scopes.instance);
    expect(trigger.scopeRef).toBe(operationalScopeRef);
    expect(trigger.definition.target.flow.scopeRef).toBe(fixture.execution.scopes.instance);
    expect(trigger.definition.executionScope ?? null).toBeNull();
    await expect(this.details).not.toContainText(operationalScopeRef);
    return identity.context.workspaceId;
  }

  public async enableAndRun(
    consoleUrl: string,
    fixture: OperationalTriggerFixture,
    operationalWorkspaceId: string,
  ): Promise<OperationalTriggerEvidence> {
    await this.openEnableDialog();
    const dialog = this.page.getByTestId(TestIds.triggers.enableDialog);
    await this.page.getByTestId(TestIds.triggers.enableConfirm).click();
    let enabled: TriggerWire | undefined;
    await expect.poll(async () => {
      enabled = await this.readTrigger(consoleUrl, fixture);
      return enabled.definition.enabled;
    }).toBe(true);
    if (!enabled) throw new Error('The Trigger did not become enabled.');
    expect(enabled.definition.executionScope?.workspaceId).toBe(operationalWorkspaceId);
    expect(enabled.definition.executionScope?.logicalScopeRef).toBe(fixture.execution.scopes.instance);
    const fired = await this.page.request.post(this.triggerUrl(consoleUrl, fixture, '/run'), {
      headers: this.contextHeaders(),
    });
    const firedBody = await fired.text();
    expect(fired.status(), firedBody).toBe(202);
    const occurrence = JSON.parse(firedBody) as TriggerOccurrenceWire;
    expect(occurrenceOutcome(occurrence).toLowerCase()).toBe('submitted');

    expect(occurrence.workspaceId).toBe(operationalWorkspaceId);
    expect(occurrence.workItemId).toBeTruthy();
    const executionScopeRef = `/workspaces/${operationalWorkspaceId}`;
    const workItemId = occurrence.workItemId!;
    const workUrl = `${consoleUrl}/api/work/workitems/${encodeURIComponent(workItemId)}?scopeRef=${encodeURIComponent(executionScopeRef)}`;
    await expect.poll(async () => workStatus(await this.read<WorkItemWire>(workUrl)), { timeout: 90_000 })
      .toMatch(/^(completed|failed|cancelled)$/);
    const work = await this.read<WorkItemWire>(workUrl);
    expect(workStatus(work), JSON.stringify(work)).toBe('completed');
    expect(work.executionScopeRef).toBe(executionScopeRef);
    let flowRun: FlowRunWire | undefined;
    await expect.poll(async () => {
      const page = await this.read<FlowRunPageWire>(
        `${consoleUrl}/api/flowRuns?scopeRef=${encodeURIComponent(executionScopeRef)}&top=200`,
      );
      flowRun = page.value.find(value => value.workItemResourceId === workItemId);
      return flowRun === undefined ? 'missing' : flowStatus(flowRun);
    }, { timeout: 90_000 }).toBe('succeeded');
    if (!flowRun) throw new Error(`The Trigger Work Item '${workItemId}' did not expose its Flow Run.`);
    expect(flowRun.executionScopeRef).toBe(executionScopeRef);
    expect(flowRun.definitionScopeRef).toBe(fixture.execution.scopes.instance);
    await this.page.getByTestId(TestIds.triggers.refreshOccurrences).click();
    await expect(this.page.locator(
      `[data-testid="${TestIds.triggers.occurrenceRow}"][data-trigger-occurrence-id="${occurrence.id}"]`,
    )).toBeVisible();
    await this.open(
      `${consoleUrl}/flow-runs/${encodeURIComponent(flowRun.id)}?scopeRef=${encodeURIComponent(executionScopeRef)}`,
      TestIds.flowObservability.runDetails,
    );
    await this.open(`${consoleUrl}/triggers/${encodeURIComponent(fixture.triggerName)}`, TestIds.resourceAdministration.triggerDetails);
    return {
      triggerUid: occurrence.triggerUid,
      occurrenceId: occurrence.id,
      operationalWorkspaceId,
      workItemId,
      flowRunId: flowRun.id,
    };
  }

  public async assertBusinessWorkspaceIsolation(
    consoleUrl: string,
    fixture: OperationalTriggerFixture,
    evidence: OperationalTriggerEvidence,
  ): Promise<void> {
    await this.selectContext(consoleUrl, fixture.execution.scopes.workspace);
    await this.open(`${consoleUrl}/triggers`, TestIds.resourceAdministration.triggers);
    await expect(this.triggerRow(fixture.triggerName)).toHaveCount(0);
    const direct = await this.page.request.get(this.triggerUrl(consoleUrl, fixture), {
      headers: this.contextHeaders(),
    });
    expect(direct.status(), await direct.text()).toBe(404);
    const work = await this.page.request.get(
      `${consoleUrl}/api/work/workitems/${encodeURIComponent(evidence.workItemId)}`,
      { headers: this.contextHeaders() },
    );
    expect(work.status(), await work.text()).toBe(404);
    const run = await this.page.request.get(`${consoleUrl}/api/flowRuns/${encodeURIComponent(evidence.flowRunId)}`, {
      headers: this.contextHeaders(),
    });
    expect(run.status(), await run.text()).toBe(404);
    await this.open(`${consoleUrl}/flow-runs`, TestIds.flowObservability.runs);
    await expect(this.page.locator(`a[href^="/flow-runs/${evidence.flowRunId}?"]`)).toHaveCount(0);
  }

  public async assertDeniedAccessFailures(consoleUrl: string, fixture: OperationalTriggerFixture): Promise<void> {
    const flow = await this.resolveFlowIdentity(consoleUrl, fixture);
    try {
      await this.setFlowAccess(consoleUrl, fixture, flow, ['discover', 'use']);
      await this.openCreateInContext(consoleUrl, fixture, fixture.execution.scopes.tenant);
      await this.fillEditor(fixture, 'tenant-use-denied-trigger', 'Tenant use denied Trigger');
      await this.setFlowAccess(consoleUrl, fixture, flow, ['discover']);
      await Promise.all([
        this.page.waitForURL(/\/triggers\/tenant-use-denied-trigger(?:\?.*)?$/, { timeout: 30_000 }),
        this.page.getByTestId(TestIds.triggers.create).click(),
      ]);
      await this.openEnableDialog();
      await this.page.getByTestId(TestIds.triggers.enableConfirm).click();
      await expect(this.page.getByTestId(TestIds.triggers.error)).toContainText(
        'This context is not authorized to use the selected Flow.',
      );

      await this.setFlowAccess(consoleUrl, fixture, flow, ['discover', 'use']);
      await this.openCreateInContext(consoleUrl, fixture, fixture.execution.scopes.tenant);
      await this.fillEditor(fixture, 'tenant-discover-denied-trigger', 'Tenant discover denied Trigger');
      await this.setFlowAccess(consoleUrl, fixture, flow, ['use']);
      await this.page.getByTestId(TestIds.triggers.create).click();
      await expect(this.page.getByTestId(TestIds.triggers.error)).toContainText(
        'This Flow is no longer visible from the selected context.',
      );
    } finally {
      await this.setFlowAccess(consoleUrl, fixture, flow, ['discover', 'use']);
    }
  }

  private async openEnableDialog(): Promise<void> {
    const enable = this.page.getByTestId(TestIds.triggers.enable);
    const dialog = this.page.getByTestId(TestIds.triggers.enableDialog);

    // The details page is server-rendered before its Blazor circuit is interactive.
    // Retry the user action so a click received during that short hydration window
    // cannot leave the journey waiting forever for a dialog that was never opened.
    await expect(async () => {
      await expect(enable).toBeEnabled();
      await enable.click();
      await expect(dialog).toBeVisible();
    }).toPass({ timeout: 30_000 });
  }

  public async assertResponsiveDetails(): Promise<void> {
    const box = await this.details.boundingBox();
    const viewport = this.page.viewportSize();
    expect(box).not.toBeNull();
    expect(viewport).not.toBeNull();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(viewport!.width);
  }

  private triggerRow(name: string): Locator {
    return this.page.locator(`[data-testid="${TestIds.triggers.row}"][data-trigger-name="${name}"]`);
  }

  private async readTrigger(consoleUrl: string, fixture: OperationalTriggerFixture): Promise<TriggerWire> {
    return await this.read<TriggerWire>(this.triggerUrl(consoleUrl, fixture));
  }

  private async openCreateInContext(
    consoleUrl: string,
    fixture: OperationalTriggerFixture,
    scopeRef: string,
  ): Promise<void> {
    await this.selectContext(consoleUrl, scopeRef);
    await this.open(`${consoleUrl}/triggers/new`, TestIds.resourceAdministration.triggerEditor);
    await expect(this.page.getByTestId(TestIds.triggers.flow).locator('option', {
      hasText: fixture.execution.flowName,
    })).toHaveCount(1);
  }

  private async fillEditor(
    fixture: OperationalTriggerFixture,
    nameValue: string,
    displayNameValue: string,
  ): Promise<void> {
    const name = this.page.getByTestId(TestIds.triggers.name);
    const displayName = this.page.getByTestId(TestIds.triggers.displayName);
    await expect(async () => {
      await fillAndCommit(name, nameValue);
      await fillAndCommit(displayName, displayNameValue);
      await expect(name).toHaveValue(nameValue);
      await expect(displayName).toHaveValue(displayNameValue);
    }).toPass();
    await this.page.getByTestId(TestIds.triggers.flow).selectOption(
      `${fixture.execution.scopes.instance}|default|${fixture.execution.flowName}`,
    );
  }

  private async resolveFlowIdentity(
    consoleUrl: string,
    fixture: OperationalTriggerFixture,
  ): Promise<ResourceIdentityWire> {
    const response = await this.page.request.get(`${consoleUrl}/api/resource-scopes`, {
      headers: { 'X-Agentstration-Context': fixture.execution.scopes.instance },
    });
    const body = await response.text();
    expect(response.status(), body).toBe(200);
    const inventory = JSON.parse(body) as {
      scopes: Array<{ scopeRef: string; resources: Array<ResourceIdentityWire & { name: string }> }>;
    };
    const flow = inventory.scopes.find(value => value.scopeRef === fixture.execution.scopes.instance)
      ?.resources.find(value => value.kind === 'Flow' && value.name === fixture.execution.flowName);
    if (!flow) throw new Error(`Flow '${fixture.execution.flowName}' was not found in the Instance inventory.`);
    return flow;
  }

  private async setFlowAccess(
    consoleUrl: string,
    fixture: OperationalTriggerFixture,
    flow: ResourceIdentityWire,
    actions: string[],
  ): Promise<void> {
    const url = `${consoleUrl}/api/resource-access-policies/${flow.uid}`;
    const headers = { 'X-Agentstration-Context': fixture.execution.scopes.instance };
    const current = await this.page.request.get(url, { headers });
    expect([200, 404]).toContain(current.status());
    const policy = current.status() === 200 ? await current.json() as AccessPolicyWire : { grants: [] };
    const grants = policy.grants.filter(value => value.targetScopeRef !== fixture.execution.scopes.tenant);
    if (actions.length > 0) {
      grants.push({
        targetScopeRef: fixture.execution.scopes.tenant,
        actions,
        includeDescendants: true,
      });
    }
    const saved = await this.page.request.put(url, {
      headers: {
        ...headers,
        ...(current.status() === 200 && current.headers()['etag'] ? { 'If-Match': current.headers()['etag']! } : {}),
      },
      data: {
        resource: {
          uid: flow.uid,
          ownerScopeRef: fixture.execution.scopes.instance,
          namespace: flow.namespace,
          kind: flow.kind,
          version: flow.version,
        },
        grants,
      },
    });
    expect(saved.status(), await saved.text()).toBe(200);
  }

  private triggerUrl(consoleUrl: string, fixture: OperationalTriggerFixture, suffix = ''): string {
    return `${consoleUrl}/api/triggers/${encodeURIComponent(fixture.triggerName)}${suffix}`;
  }

  private async selectContext(consoleUrl: string, scopeRef: string): Promise<void> {
    this.selectedScopeRef = scopeRef;
    const context = await this.read<IdentityContextWire>(`${consoleUrl}/api/identity/context`);
    expect(context.selectedScopeRef).toBe(scopeRef);
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
    if (!response?.ok()) throw new Error(`Operational Trigger route '${url}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(marker).waitFor({ state: 'visible' });
  }

  private contextHeaders(): Record<string, string> {
    return this.selectedScopeRef ? { 'X-Agentstration-Context': this.selectedScopeRef } : {};
  }
}

function occurrenceOutcome(occurrence: TriggerOccurrenceWire | undefined): string {
  if (!occurrence) return 'Missing';
  return typeof occurrence.outcome === 'string'
    ? occurrence.outcome
    : ['Pending', 'Submitted', 'Skipped', 'Failed'][occurrence.outcome] ?? `${occurrence.outcome}`;
}

function workStatus(work: WorkItemWire): string {
  return typeof work.status === 'string'
    ? work.status.toLowerCase()
    : ['pending', 'queued', 'running', 'waitingforinput', 'waitingforapproval', 'paused', 'completed', 'failed', 'cancelled'][work.status] ?? `${work.status}`;
}

function flowStatus(run: FlowRunWire): string {
  return typeof run.status === 'string'
    ? run.status.toLowerCase()
    : ['pending', 'running', 'waitingforinput', 'succeeded', 'failed', 'cancelled'][run.status] ?? `${run.status}`;
}

