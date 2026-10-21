import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { LoginPage } from './login.page.js';

interface IdentityContextWire {
  context: { principalId: string; tenantId: string; workspaceId: string };
  availableWorkspaces: Array<{ id: string; tenantId: string; tenantName: string; name: string }>;
}

interface AgentWire {
  uid: string;
  generation: number;
  namespace: string;
  kind: string;
}

interface RuntimeRunWire {
  id: string;
  executionScopeRef: string;
  status: { state: string; errorCode?: string | null };
  properties: { agent: { scopeRef?: string } };
}

interface FlowRunWire {
  id: string;
  status: string | number;
  definitionScopeRef?: string;
  executionScopeRef: string;
  workItemResourceId?: string;
}

interface WorkItemWire {
  id: string;
  status: string | number;
  executionScopeRef: string;
}

export interface ExecutionScopeFixture {
  agentName: string;
  flowName: string;
  agentUid: string;
  agentGeneration: number;
  tenantId: string;
  workspaceId: string;
  siblingWorkspaceId: string;
  siblingTenantId: string;
  siblingTenantWorkspaceId: string;
  scopes: { instance: string; tenant: string; workspace: string };
}

export interface ScopedExecutionEvidence {
  scopeRef: string;
  runtimeRunId: string;
  flowRunId: string;
  workItemId: string;
}

const agentName = 'hierarchical-execution-agent';
const flowName = 'hierarchical-execution-flow';
const modelProviderName = 'execution-scope-ollama';
const modelProfileName = 'execution-scope-reasoning';
const siblingWorkspaceName = 'execution-scope-sibling';
const siblingProfileName = 'playwright-execution-scope-sibling';

export class ExecutionScopesPage {
  public constructor(private readonly page: Page) {}

  public async ensureFixture(consoleUrl: string): Promise<ExecutionScopeFixture> {
    const original = await this.identityContext(consoleUrl);
    await this.ensureSiblingTenant(consoleUrl);
    await this.ensureSiblingWorkspace(consoleUrl, original.context.tenantId);
    const context = await this.identityContext(consoleUrl);
    const siblingWorkspace = context.availableWorkspaces.find(value =>
      value.tenantId === original.context.tenantId && value.name === siblingWorkspaceName);
    const siblingTenantWorkspace = context.availableWorkspaces.find(value =>
      value.tenantName === 'playwright-sibling' && value.name === 'playwright-sibling');
    if (!siblingWorkspace || !siblingTenantWorkspace) {
      throw new Error('The execution-scope fixture did not expose both sibling Workspaces.');
    }

    const scopes = {
      instance: '/instance',
      tenant: `/tenants/${original.context.tenantId}`,
      workspace: `/workspaces/${original.context.workspaceId}`,
    };
    await this.ensureInstanceModelProfile(consoleUrl, scopes.tenant);
    const agent = await this.ensureInstanceAgent(consoleUrl, scopes.tenant);
    await this.ensureInstanceFlow(consoleUrl, agent, scopes.tenant);

    return {
      agentName,
      flowName,
      agentUid: agent.uid,
      agentGeneration: agent.generation,
      tenantId: original.context.tenantId,
      workspaceId: original.context.workspaceId,
      siblingWorkspaceId: siblingWorkspace.id,
      siblingTenantId: siblingTenantWorkspace.tenantId,
      siblingTenantWorkspaceId: siblingTenantWorkspace.id,
      scopes,
    };
  }

  public async executeAtScopes(consoleUrl: string, fixture: ExecutionScopeFixture): Promise<ScopedExecutionEvidence[]> {
    const evidence: ScopedExecutionEvidence[] = [];
    for (const scopeRef of Object.values(fixture.scopes)) {
      evidence.push(await this.executeAtScope(consoleUrl, fixture, scopeRef));
    }
    return evidence;
  }

  public async executeAtScope(consoleUrl: string, fixture: ExecutionScopeFixture, scopeRef: string): Promise<ScopedExecutionEvidence> {
    const runtime = await this.createRuntimeRun(consoleUrl, fixture, scopeRef);
    const flow = await this.createFlowRun(consoleUrl, fixture, scopeRef);
    if (!flow.workItemResourceId) throw new Error(`Flow Run '${flow.id}' did not expose its WorkItem.`);
    const work = await this.waitForWorkItem(consoleUrl, flow.workItemResourceId, scopeRef);
    expect(runtime.executionScopeRef).toBe(scopeRef);
    expect(runtime.properties.agent.scopeRef).toBe('/instance');
    expect(flow.executionScopeRef).toBe(scopeRef);
    expect(flow.definitionScopeRef).toBe('/instance');
    expect(work.executionScopeRef).toBe(scopeRef);
    return { scopeRef, runtimeRunId: runtime.id, flowRunId: flow.id, workItemId: work.id };
  }

  public async openRuntimeRun(consoleUrl: string, evidence: ScopedExecutionEvidence): Promise<void> {
    await this.open(
      `${consoleUrl}/runs/${encodeURIComponent(evidence.runtimeRunId)}?scopeRef=${encodeURIComponent(evidence.scopeRef)}`,
      TestIds.flowObservability.agentRunner,
    );
    await expect(this.agentDefinitionScope).toContainText('/instance');
    await expect(this.agentExecutionScope).toContainText(evidence.scopeRef);
  }

  public async openFlowRun(consoleUrl: string, evidence: ScopedExecutionEvidence): Promise<void> {
    await this.open(
      `${consoleUrl}/flow-runs/${encodeURIComponent(evidence.flowRunId)}?scopeRef=${encodeURIComponent(evidence.scopeRef)}`,
      TestIds.flowObservability.runDetails,
    );
    await expect(this.flowRunDefinitionScope).toContainText('/instance');
    await expect(this.flowRunExecutionScope).toContainText(evidence.scopeRef);
  }

  public async expectExactIsolation(consoleUrl: string, fixture: ExecutionScopeFixture, evidence: ScopedExecutionEvidence[]): Promise<void> {
    for (const item of evidence) {
      for (const otherScope of Object.values(fixture.scopes).filter(scope => scope !== item.scopeRef)) {
        await this.expectNotFound(`${consoleUrl}/api/runtime/runs/${encodeURIComponent(item.runtimeRunId)}?scopeRef=${encodeURIComponent(otherScope)}`);
        await this.expectNotFound(`${consoleUrl}/api/flowRuns/${encodeURIComponent(item.flowRunId)}?scopeRef=${encodeURIComponent(otherScope)}`);
        await this.expectNotFound(`${consoleUrl}/api/work/workitems/${encodeURIComponent(item.workItemId)}?scopeRef=${encodeURIComponent(otherScope)}`);
      }
    }

    await this.selectWorkspace(consoleUrl, fixture.siblingWorkspaceId);
    for (const item of evidence) {
      await this.expectNotFound(`${consoleUrl}/api/runtime/runs/${encodeURIComponent(item.runtimeRunId)}`);
      await this.expectNotFound(`${consoleUrl}/api/flowRuns/${encodeURIComponent(item.flowRunId)}`);
      await this.expectNotFound(`${consoleUrl}/api/work/workitems/${encodeURIComponent(item.workItemId)}`);
    }

    await this.expectUnrelatedTenantDenied(consoleUrl, fixture, evidence);

    await this.selectWorkspace(consoleUrl, fixture.workspaceId);
  }

  public async assertLocalizedLabels(consoleUrl: string, evidence: ScopedExecutionEvidence): Promise<void> {
    await this.page.request.put(`${consoleUrl}/api/identity/preferences`, { data: { theme: 'Dark', language: 'fr-FR' } });
    await this.page.request.get(`${consoleUrl}/_culture?culture=fr-FR&returnUrl=%2F`);
    await this.openFlowRun(consoleUrl, evidence);
    await expect(this.flowRunDefinitionScope.getByText('Périmètre de définition', { exact: true })).toBeVisible();
    await expect(this.flowRunExecutionScope.getByText('Périmètre d’exécution', { exact: true })).toBeVisible();
    await this.page.request.put(`${consoleUrl}/api/identity/preferences`, { data: { theme: 'Dark', language: 'en-US' } });
    await this.page.request.get(`${consoleUrl}/_culture?culture=en-US&returnUrl=%2F`);
    await this.openFlowRun(consoleUrl, evidence);
    await expect(this.flowRunDefinitionScope.getByText('Definition scope', { exact: true })).toBeVisible();
    await expect(this.flowRunExecutionScope.getByText('Execution scope', { exact: true })).toBeVisible();
  }

  public get agentDefinitionScope(): Locator { return this.page.getByTestId(TestIds.flowObservability.agentDefinitionScope); }
  public get agentExecutionScope(): Locator { return this.page.getByTestId(TestIds.flowObservability.agentExecutionScope); }
  public get agentRunDefinitionScope(): Locator { return this.page.getByTestId(TestIds.flowObservability.agentRunDefinitionScope); }
  public get agentRunExecutionScope(): Locator { return this.page.getByTestId(TestIds.flowObservability.agentRunExecutionScope); }
  public get flowRunDefinitionScope(): Locator { return this.page.getByTestId(TestIds.flowObservability.flowRunDefinitionScope); }
  public get flowRunExecutionScope(): Locator { return this.page.getByTestId(TestIds.flowObservability.flowRunExecutionScope); }

  private async ensureSiblingTenant(consoleUrl: string): Promise<void> {
    const catalog = await this.read<{ catalog: { profiles: Array<{ name: string }> } }>(`${consoleUrl}/api/bootstrap/profiles`);
    expect(catalog.catalog.profiles.some(profile => profile.name === siblingProfileName)).toBe(true);
    const previewResponse = await this.page.request.post(`${consoleUrl}/api/bootstrap/profiles/preview`, {
      data: { profiles: [siblingProfileName] },
    });
    const previewText = await previewResponse.text();
    expect(previewResponse.status(), previewText).toBe(200);
    const preview = JSON.parse(previewText) as { digest: string; resources: Array<{ disposition: string | number }> };
    if (preview.resources.every(resource => resource.disposition === 'Skip' || resource.disposition === 1)) return;
    const apply = await this.page.request.post(`${consoleUrl}/api/bootstrap/applications`, {
      data: { profiles: [siblingProfileName], expectedDigest: preview.digest },
    });
    expect(apply.status(), await apply.text()).toBe(201);
  }

  private async ensureSiblingWorkspace(consoleUrl: string, tenantId: string): Promise<void> {
    const context = await this.identityContext(consoleUrl);
    if (context.availableWorkspaces.some(value => value.tenantId === tenantId && value.name === siblingWorkspaceName)) return;
    const created = await this.page.request.post(`${consoleUrl}/api/identity/workspaces`, {
      data: { name: siblingWorkspaceName, displayName: 'Execution scope sibling Workspace' },
    });
    expect(created.status(), await created.text()).toBe(201);
  }

  private async ensureInstanceAgent(consoleUrl: string, tenantScope: string): Promise<AgentWire> {
    const url = `${consoleUrl}/api/agents/${agentName}?scopeRef=${encodeURIComponent('/instance')}`;
    const current = await this.page.request.get(url);
    let agent: AgentWire;
    if (current.status() === 404) {
      const created = await this.page.request.put(`${consoleUrl}/api/agents/${agentName}`, {
        headers: { 'If-None-Match': '*' },
        data: {
          apiVersion: 'agentstration.io/v1', kind: 'Agent', scopeRef: '/instance',
          metadata: { name: agentName, namespace: 'default' },
          definition: {
            displayName: 'Hierarchical execution Agent',
            description: 'Deterministic Instance Agent for exact execution-scope browser coverage.',
            instructions: 'Return a concise deterministic response.',
            modelProfile: { name: modelProfileName, namespace: 'default', scopeRef: '/instance' },
            runtimeProfile: { name: 'maf-builtin', namespace: 'default', scopeRef: '/instance' },
            tools: [], toolSets: [], behaviors: [], middleware: [], contextProviders: [], settings: {},
          },
        },
      });
      expect(created.status(), await created.text()).toBe(200);
      agent = await created.json() as AgentWire;
    } else {
      expect(current.status(), await current.text()).toBe(200);
      agent = await current.json() as AgentWire;
    }
    await this.grantDescendantUse(consoleUrl, agent, tenantScope);
    const prepared = await this.page.request.post(
      `${consoleUrl}/api/runtime/agents/${agentName}/prepare?generation=${agent.generation}&scopeRef=${encodeURIComponent('/instance')}`,
    );
    expect(prepared.status(), await prepared.text()).toBe(200);
    return agent;
  }

  private async ensureInstanceModelProfile(consoleUrl: string, tenantScope: string): Promise<void> {
    const providerUrl = `${consoleUrl}/api/modelproviders/${modelProviderName}?scopeRef=${encodeURIComponent('/instance')}`;
    const currentProvider = await this.page.request.get(providerUrl);
    let provider: AgentWire;
    if (currentProvider.status() === 404) {
      const created = await this.page.request.post(`${consoleUrl}/api/modelproviders`, {
        data: {
          name: modelProviderName,
          namespace: 'default',
          scopeRef: '/instance',
          properties: {
            displayName: 'Execution scope Ollama',
            extension: { name: 'ollama-extension', namespace: 'default' },
            contributionId: 'ollama',
            valueBindings: [],
            specificationOverrides: {},
          },
        },
      });
      expect(created.status(), await created.text()).toBe(201);
      provider = await created.json() as AgentWire;
    } else {
      expect(currentProvider.status(), await currentProvider.text()).toBe(200);
      provider = await currentProvider.json() as AgentWire;
    }
    await this.grantDescendantUse(consoleUrl, provider, tenantScope);
    const refreshed = await this.page.request.post(`${consoleUrl}/api/modelproviders/${modelProviderName}/models/refresh`);
    expect(refreshed.status(), await refreshed.text()).toBe(200);

    const profileUrl = `${consoleUrl}/api/modelprofiles/${modelProfileName}?scopeRef=${encodeURIComponent('/instance')}`;
    const currentProfile = await this.page.request.get(profileUrl);
    let profile: AgentWire;
    if (currentProfile.status() === 404) {
      const created = await this.page.request.post(`${consoleUrl}/api/modelprofiles`, {
        data: {
          name: modelProfileName,
          namespace: 'default',
          scopeRef: '/instance',
          properties: {
            displayName: 'Execution scope reasoning',
            description: 'Deterministic Instance model profile for exact execution-scope browser coverage.',
            provider: { name: modelProviderName, namespace: 'default', scopeRef: '/instance' },
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
      profile = await created.json() as AgentWire;
    } else {
      expect(currentProfile.status(), await currentProfile.text()).toBe(200);
      profile = await currentProfile.json() as AgentWire;
    }
    await this.grantDescendantUse(consoleUrl, profile, tenantScope);
  }

  private async ensureInstanceFlow(consoleUrl: string, agent: AgentWire, tenantScope: string): Promise<void> {
    const current = await this.page.request.get(`${consoleUrl}/api/flows/${flowName}?scopeRef=${encodeURIComponent('/instance')}`);
    if (current.status() === 404) {
      const created = await this.page.request.post(`${consoleUrl}/api/flows`, {
        data: {
          name: flowName, displayName: 'Hierarchical execution Flow',
          description: 'Deterministic Instance Flow for exact execution-scope browser coverage.',
          version: '1.0.0', enabled: true, scopeRef: '/instance',
          definition: {
            flowKind: 'direct',
            target: { kind: 0, id: agentName, namespace: 'default', scopeRef: '/instance', resourceUid: agent.uid },
          },
        },
      });
      expect(created.status(), await created.text()).toBe(201);
      const published = await this.page.request.post(
        `${consoleUrl}/api/flows/${flowName}/versions?scopeRef=${encodeURIComponent('/instance')}`,
        { data: { version: '1.0.0', activate: true } },
      );
      expect(published.status(), await published.text()).toBe(201);
    } else {
      expect(current.status(), await current.text()).toBe(200);
    }
    await this.open(`${consoleUrl}/flows/${flowName}?scopeRef=${encodeURIComponent('/instance')}`, TestIds.flowObservability.flowDetails);
    await expect(async () => {
      await this.page.getByTestId(TestIds.flowObservability.flowAccessTab).click();
      await expect(this.page.getByTestId(TestIds.resourceAccess.panel)).toBeVisible();
    }).toPass({ timeout: 30_000 });
    if (await this.page.locator(`[data-testid="${TestIds.resourceAccess.grant}"][data-scope-ref="${tenantScope}"]`).count() === 0) {
      await this.page.getByTestId(TestIds.resourceAccess.target).selectOption(tenantScope);
      await this.page.getByTestId(TestIds.resourceAccess.add).click();
      await this.page.locator(`[data-testid="${TestIds.resourceAccess.grant}"][data-scope-ref="${tenantScope}"]`).waitFor({ state: 'visible' });
      await this.page.getByTestId(TestIds.resourceAccess.save).click();
      await this.page.getByTestId(TestIds.resourceAccess.saved).waitFor({ state: 'visible' });
    }
  }

  private async grantDescendantUse(consoleUrl: string, resource: AgentWire, tenantScope: string): Promise<void> {
    const url = `${consoleUrl}/api/resource-access-policies/${resource.uid}`;
    const current = await this.page.request.get(url);
    const grants = current.status() === 404
      ? []
      : ((await current.json()) as { grants: Array<{ targetScopeRef: string; actions: string[]; includeDescendants: boolean }> }).grants;
    if (grants.some(grant => grant.targetScopeRef === tenantScope
      && grant.includeDescendants && grant.actions.includes('discover') && grant.actions.includes('use'))) return;
    const saved = await this.page.request.put(url, {
      headers: current.status() === 200 && current.headers()['etag'] ? { 'If-Match': current.headers()['etag']! } : undefined,
      data: {
        resource: {
          uid: resource.uid, ownerScopeRef: '/instance', namespace: resource.namespace,
          kind: resource.kind, generation: resource.generation,
        },
        grants: [
          ...grants.filter(grant => grant.targetScopeRef !== tenantScope),
          { targetScopeRef: tenantScope, actions: ['discover', 'use'], includeDescendants: true },
        ],
      },
    });
    expect(saved.status(), await saved.text()).toBe(200);
  }

  private async createRuntimeRun(consoleUrl: string, fixture: ExecutionScopeFixture, scopeRef: string): Promise<RuntimeRunWire> {
    const created = await this.page.request.post(`${consoleUrl}/api/runtime/runs`, {
      data: this.runtimeRunRequest(fixture, scopeRef),
    });
    const body = await created.text();
    expect(created.status(), body).toBe(202);
    const run = JSON.parse(body) as RuntimeRunWire;
    await expect.poll(async () => (await this.read<RuntimeRunWire>(
      `${consoleUrl}/api/runtime/runs/${encodeURIComponent(run.id)}?scopeRef=${encodeURIComponent(scopeRef)}`)).status.state,
    { timeout: 90_000 }).toBe('Succeeded');
    return await this.read<RuntimeRunWire>(
      `${consoleUrl}/api/runtime/runs/${encodeURIComponent(run.id)}?scopeRef=${encodeURIComponent(scopeRef)}`);
  }

  private runtimeRunRequest(fixture: ExecutionScopeFixture, scopeRef: string): object {
    return {
      executionScopeRef: scopeRef,
      agent: {
        resourceId: fixture.agentName, version: fixture.agentGeneration, namespace: 'default',
        scopeRef: '/instance', resourceUid: fixture.agentUid,
      },
      input: { messages: [{ role: 'User', content: `Execute deterministically at ${scopeRef}.` }] },
      origin: 'Api',
    };
  }

  private async createFlowRun(consoleUrl: string, fixture: ExecutionScopeFixture, scopeRef: string): Promise<FlowRunWire> {
    const created = await this.page.request.post(`${consoleUrl}/api/flows/${fixture.flowName}/runs`, {
      data: {
        input: { request: `Execute deterministically at ${scopeRef}.` },
        version: '1.0.0', definitionScopeRef: '/instance', executionScopeRef: scopeRef,
      },
    });
    const body = await created.text();
    expect(created.status(), body).toBe(202);
    const run = JSON.parse(body) as FlowRunWire;
    await expect.poll(async () => flowStatus(await this.read<FlowRunWire>(
      `${consoleUrl}/api/flowRuns/${encodeURIComponent(run.id)}?scopeRef=${encodeURIComponent(scopeRef)}`)),
    { timeout: 90_000 }).toBe('Succeeded');
    return await this.read<FlowRunWire>(
      `${consoleUrl}/api/flowRuns/${encodeURIComponent(run.id)}?scopeRef=${encodeURIComponent(scopeRef)}`);
  }

  private async waitForWorkItem(consoleUrl: string, workItemId: string, scopeRef: string): Promise<WorkItemWire> {
    const url = `${consoleUrl}/api/work/workitems/${encodeURIComponent(workItemId)}?scopeRef=${encodeURIComponent(scopeRef)}`;
    await expect.poll(async () => workStatus(await this.read<WorkItemWire>(url)), { timeout: 90_000 }).toBe('Completed');
    return await this.read<WorkItemWire>(url);
  }

  private async identityContext(consoleUrl: string): Promise<IdentityContextWire> {
    return await this.read<IdentityContextWire>(`${consoleUrl}/api/identity/context`);
  }

  private async selectWorkspace(consoleUrl: string, workspaceId: string): Promise<void> {
    const selected = await this.page.request.post(`${consoleUrl}/bff/identity/context/workspace`, { data: { workspaceId } });
    expect(selected.status(), await selected.text()).toBe(200);
  }

  private async expectUnrelatedTenantDenied(
    consoleUrl: string,
    fixture: ExecutionScopeFixture,
    evidence: ScopedExecutionEvidence[],
  ): Promise<void> {
    const suffix = Date.now().toString(36);
    const userName = `execution-scope-member-${suffix}`;
    const password = 'Execution-Scope-Member-812!';
    const accountResponse = await this.page.request.post(`${consoleUrl}/api/identity/accounts`, {
      data: {
        userName,
        password,
        displayName: 'Execution scope member',
        email: null,
        workspaceId: fixture.siblingTenantWorkspaceId,
        role: 'Member',
      },
    });
    expect(accountResponse.status(), await accountResponse.text()).toBe(201);

    const browser = this.page.context().browser();
    if (!browser) throw new Error('The browser context is unavailable.');
    const memberContext = await browser.newContext({ locale: 'en-US', colorScheme: 'dark' });
    const memberPage = await memberContext.newPage();
    try {
      await new LoginPage(memberPage).signIn(consoleUrl, userName, password);
      const selected = await memberPage.request.post(`${consoleUrl}/bff/identity/context/workspace`, {
        data: { workspaceId: fixture.siblingTenantWorkspaceId },
      });
      expect(selected.status(), await selected.text()).toBe(200);
      const memberIdentity = await memberPage.request.get(`${consoleUrl}/api/identity/context`);
      const memberIdentityBody = await memberIdentity.text();
      expect(memberIdentity.status(), memberIdentityBody).toBe(200);
      expect((JSON.parse(memberIdentityBody) as IdentityContextWire).context.tenantId).toBe(fixture.siblingTenantId);
      const deniedScope = `/workspaces/${fixture.siblingTenantWorkspaceId}`;
      const denied = await memberPage.request.post(`${consoleUrl}/api/runtime/runs`, {
        data: this.runtimeRunRequest(fixture, deniedScope),
      });
      const deniedBody = await denied.text();
      expect(denied.status(), deniedBody).toBe(400);
      expect(deniedBody).toContain('agent_not_found');
      for (const item of evidence) {
        for (const path of [
          `/api/runtime/runs/${encodeURIComponent(item.runtimeRunId)}`,
          `/api/flowRuns/${encodeURIComponent(item.flowRunId)}`,
          `/api/work/workitems/${encodeURIComponent(item.workItemId)}`,
        ]) {
          const response = await memberPage.request.get(`${consoleUrl}${path}`);
          expect(response.status(), await response.text()).toBe(404);
        }
      }
    } finally {
      await memberContext.close();
    }
  }

  private async expectNotFound(url: string): Promise<void> {
    const response = await this.page.request.get(url);
    expect(response.status(), await response.text()).toBe(404);
  }

  private async read<T>(url: string): Promise<T> {
    const response = await this.page.request.get(url);
    const body = await response.text();
    expect(response.status(), body).toBe(200);
    return JSON.parse(body) as T;
  }

  private async open(url: string, marker: string): Promise<void> {
    const response = await this.page.goto(url, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Execution-scope route '${url}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(marker).waitFor({ state: 'visible' });
  }
}

function flowStatus(run: FlowRunWire): string {
  return typeof run.status === 'string' ? run.status : ['Pending', 'Running', 'WaitingForInput', 'Succeeded', 'Failed', 'Cancelled', 'TimedOut', 'WaitingForChild'][run.status] ?? `${run.status}`;
}

function workStatus(work: WorkItemWire): string {
  return typeof work.status === 'string' ? work.status : ['Created', 'Queued', 'Running', 'WaitingForInput', 'WaitingForApproval', 'Paused', 'Completed', 'Failed', 'Cancelled'][work.status] ?? `${work.status}`;
}
