import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import type { ExecutionScopeFixture } from './execution-scopes.page.js';

interface ResourceIdentity {
  uid: string;
  generation: number;
  namespace: string;
  kind: string;
}

const duplicateAgentName = 'scoped-designer-duplicate';

export class ScopedFlowDesignerPage {
  public constructor(private readonly page: Page) {}

  public async exercise(consoleUrl: string, fixture: ExecutionScopeFixture): Promise<void> {
    const tenantAgent = await this.ensureAgent(consoleUrl, fixture.scopes.tenant, fixture);
    const workspaceAgent = await this.ensureAgent(consoleUrl, fixture.scopes.workspace, fixture);
    await this.grant(consoleUrl, fixture.scopes.tenant, tenantAgent, fixture.scopes.workspace, false);

    const instanceFlow = await this.ensureDraft(consoleUrl, 'scoped-designer-instance', fixture.scopes.instance, fixture.scopes.instance, fixture.agentName);
    const tenantFlow = await this.ensureDraft(consoleUrl, 'scoped-designer-tenant', fixture.scopes.tenant, fixture.scopes.tenant, duplicateAgentName);
    const workspaceFlow = await this.ensureDraft(consoleUrl, 'scoped-designer-workspace', fixture.scopes.workspace, fixture.scopes.instance, fixture.agentName);

    await this.open(consoleUrl, instanceFlow, fixture.scopes.instance);
    await this.expectAgentOptions(
      [`${fixture.scopes.instance}|default|${fixture.agentName}`],
      [`${fixture.scopes.tenant}|default|${duplicateAgentName}`, `${fixture.scopes.workspace}|default|${duplicateAgentName}`],
    );

    await this.open(consoleUrl, tenantFlow, fixture.scopes.tenant);
    await this.expectAgentOptions(
      [`${fixture.scopes.instance}|default|${fixture.agentName}`, `${fixture.scopes.tenant}|default|${duplicateAgentName}`],
      [`${fixture.scopes.workspace}|default|${duplicateAgentName}`],
    );

    await this.open(consoleUrl, workspaceFlow, fixture.scopes.workspace);
    await this.expectAgentOptions(
      [
        `${fixture.scopes.instance}|default|${fixture.agentName}`,
        `${fixture.scopes.tenant}|default|${duplicateAgentName}`,
        `${fixture.scopes.workspace}|default|${duplicateAgentName}`,
      ],
      [
        `/workspaces/${fixture.siblingWorkspaceId}|default|${duplicateAgentName}`,
        `/tenants/${fixture.siblingTenantId}|default|${duplicateAgentName}`,
      ],
    );

    await this.openAgentSettings();
    const selector = this.page.locator('#flow-agent-reference');
    const exactTenant = `${fixture.scopes.tenant}|default|${duplicateAgentName}`;
    await selector.selectOption(exactTenant);
    await expect(selector).toHaveValue(exactTenant);
    await this.page.locator('.step-settings-close').click();
    await expect(this.page.locator('.save-state.saved')).toBeVisible({ timeout: 30_000 });
    await this.page.reload({ waitUntil: 'domcontentloaded' });
    await this.openAgentSettings();
    await expect(this.page.locator('#flow-agent-reference')).toHaveValue(exactTenant);
    await this.page.locator('.step-settings-close').click();

    const validation = await this.page.request.post(
      `${consoleUrl}/api/flows/${workspaceFlow}/validate?scopeRef=${encodeURIComponent(fixture.scopes.workspace)}`,
      { headers: { 'X-Agentstration-Context': fixture.scopes.workspace } },
    );
    expect(validation.status(), await validation.text()).toBe(200);
    const validationResult = await validation.json() as { issues: unknown[] };
    expect(validationResult.issues, JSON.stringify(validationResult.issues)).toEqual([]);

    const version = `1.0.${Date.now() % 1_000_000}`;
    await this.page.getByLabel(/Publish version|Version de publication/i).fill(version);
    await this.page.getByTestId(TestIds.flowObservability.designerPublish).click();
    const success = this.page.getByTestId(TestIds.flowObservability.designerPublicationSuccess);
    await expect(success).toContainText(version, { timeout: 30_000 });
    await expect(success).toContainText(/activated|activée/i);

    await this.page.getByLabel(/Publish version|Version de publication/i).fill(version);
    await this.page.getByTestId(TestIds.flowObservability.designerPublish).click();
    await expect(this.publicationError).toContainText(version);
    await expect(this.page.getByTestId(TestIds.flowObservability.designerPublicationSuccess)).toHaveCount(0);
  }

  public get designer(): Locator { return this.page.getByTestId(TestIds.flowObservability.designer); }
  public get publicationError(): Locator { return this.page.getByTestId(TestIds.flowObservability.designerPublicationError); }

  private async ensureAgent(consoleUrl: string, scopeRef: string, fixture: ExecutionScopeFixture): Promise<ResourceIdentity> {
    const headers = { 'X-Agentstration-Context': scopeRef };
    const url = `${consoleUrl}/api/agents/${duplicateAgentName}?scopeRef=${encodeURIComponent(scopeRef)}`;
    const current = await this.page.request.get(url, { headers });
    if (current.status() === 200) return await current.json() as ResourceIdentity;
    expect(current.status(), await current.text()).toBe(404);
    const created = await this.page.request.put(`${consoleUrl}/api/agents/${duplicateAgentName}`, {
      headers: { ...headers, 'If-None-Match': '*' },
      data: {
        apiVersion: 'agentstration.io/v1', kind: 'Agent', scopeRef,
          metadata: { name: duplicateAgentName, namespace: 'default' },
        definition: {
          displayName: `Scoped duplicate ${scopeRef}`,
          description: 'Exact-scope Flow Designer browser fixture.',
          handler: 'prompt-agent', instructions: 'Return a deterministic response.',
          modelProfile: { name: fixture.modelProfileName, namespace: 'default', scopeRef: fixture.scopes.instance },
          runtimeProfile: { name: 'maf-builtin', namespace: 'default', scopeRef: fixture.scopes.instance },
          tools: [], toolSets: [], behaviors: [], middleware: [], contextProviders: [], settings: {},
        },
      },
    });
    expect(created.status(), await created.text()).toBe(200);
    return await created.json() as ResourceIdentity;
  }

  private async ensureDraft(
    consoleUrl: string,
    name: string,
    ownerScope: string,
    selectedAgentScope: string,
    selectedAgentName: string,
  ): Promise<string> {
    const headers = { 'X-Agentstration-Context': ownerScope };
    const query = `scopeRef=${encodeURIComponent(ownerScope)}`;
    let draft = await this.page.request.get(`${consoleUrl}/api/flows/${name}/draft?${query}`, { headers });
    if (draft.status() === 404) {
      const created = await this.page.request.post(`${consoleUrl}/api/flows/drafts`, {
        headers,
        data: { name, displayName: name, template: 'Empty', scopeRef: ownerScope },
      });
      expect(created.status(), await created.text()).toBe(201);
      draft = await this.page.request.get(`${consoleUrl}/api/flows/${name}/draft?${query}`, { headers });
    }
    expect(draft.status(), await draft.text()).toBe(200);
    const response = await draft.json() as { value: Record<string, unknown>; eTag: string };
    const definition = {
      entryStep: 'input',
      steps: [
        { type: 'input', name: 'input', displayName: 'Input' },
        {
          type: 'agent', name: 'agent', displayName: 'Scoped Agent',
          agent: { resourceId: selectedAgentName, namespace: 'default', scopeRef: selectedAgentScope },
          inputMapping: '${transition.output}',
        },
        { type: 'output', name: 'completed', displayName: 'Completed', outcome: 'success', outputMapping: '${transition.output}' },
        { type: 'output', name: 'error', displayName: 'Error', outcome: 'error', code: 'FLOW_ERROR', message: 'Flow failed.' },
      ],
      transitions: [
        { id: 'input-completed-agent', fromStep: 'input', event: 'completed', toStep: 'agent' },
        { id: 'agent-success-completed', fromStep: 'agent', event: 'success', toStep: 'completed' },
        { id: 'agent-error-error', fromStep: 'agent', event: 'error', toStep: 'error' },
      ],
      designer: { preferredLayout: 'Horizontal', nodePositions: {
        input: { x: 60, y: 240 }, agent: { x: 380, y: 240 }, completed: { x: 720, y: 100 }, error: { x: 720, y: 380 },
      } },
    };
    const updated = await this.page.request.put(`${consoleUrl}/api/flows/${name}/draft?${query}`, {
      headers: { ...headers, 'If-Match': response.eTag },
      data: {
        displayName: response.value.displayName,
        description: response.value.description,
        tags: response.value.tags,
        definition,
      },
    });
    expect(updated.status(), await updated.text()).toBe(200);
    return name;
  }

  private async grant(
    consoleUrl: string,
    ownerScope: string,
    resource: ResourceIdentity,
    targetScope: string,
    includeDescendants: boolean,
  ): Promise<void> {
    const url = `${consoleUrl}/api/resource-access-policies/${resource.uid}`;
    const headers = { 'X-Agentstration-Context': ownerScope };
    const current = await this.page.request.get(url, { headers });
    const grants = current.status() === 404 ? [] : (await current.json() as {
      grants: Array<{ targetScopeRef: string; actions: string[]; includeDescendants: boolean }>;
    }).grants;
    if (grants.some(value => value.targetScopeRef === targetScope && value.actions.includes('use'))) return;
    const saved = await this.page.request.put(url, {
      headers: { ...headers, ...(current.headers()['etag'] ? { 'If-Match': current.headers()['etag']! } : {}) },
      data: {
        resource: { uid: resource.uid, ownerScopeRef: ownerScope, namespace: resource.namespace, kind: resource.kind, generation: resource.generation },
        grants: [...grants, { targetScopeRef: targetScope, actions: ['discover', 'use'], includeDescendants }],
      },
    });
    expect(saved.status(), await saved.text()).toBe(200);
  }

  private async open(consoleUrl: string, name: string, scopeRef: string): Promise<void> {
    const url = `${consoleUrl}/namespaces/default/flows/${name}/designer?scopeRef=${encodeURIComponent(scopeRef)}&context=${encodeURIComponent(scopeRef)}`;
    const response = await this.page.goto(url, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Scoped Flow Designer returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.designer.waitFor({ state: 'visible' });
    await this.openAgentSettings();
  }

  private async openAgentSettings(): Promise<void> {
    const dialog = this.page.locator('.step-settings-dialog');
    const configure = this.page.locator('.flow-node.agent .flow-node-settings');
    await expect(async () => {
      if (!await dialog.isVisible()) await configure.click();
      await expect(dialog).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
    await this.page.getByTestId(TestIds.flowObservability.stepSettingsConfigurationTab).click();
    await expect(this.page.locator('#flow-agent-reference')).toBeVisible();
  }

  private async expectAgentOptions(included: readonly string[], excluded: readonly string[]): Promise<void> {
    const values = await this.page.locator('#flow-agent-reference option').evaluateAll(options =>
      options.map(option => (option as HTMLOptionElement).value));
    for (const reference of included) expect(values).toContain(reference);
    for (const reference of excluded) expect(values).not.toContain(reference);
    await this.page.locator('.step-settings-close').click();
  }
}
