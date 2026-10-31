import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { LoginPage } from './login.page.js';
import type { ExecutionScopeFixture } from './execution-scopes.page.js';

interface AgentWire {
  uid: string;
  generation: number;
  namespace: string;
  kind: string;
}

interface IdentityContextWire {
  availableContexts: Array<{ scopeRef: string }>;
}

const workspaceAgentName = 'model-profile-usage-workspace-agent';
const siblingAgentName = 'model-profile-usage-sibling-agent';
const instanceAgentName = 'model-profile-usage-instance-agent';
const profileName = 'model-profile-cross-scope';

export class ModelProfileCrossScopePage {
  public constructor(private readonly page: Page) {}

  public get usagesPanel(): Locator {
    return this.page.getByTestId(TestIds.modelAdministration.profileUsagesPanel);
  }

  public async ensureFixture(
    consoleUrl: string,
    base: ExecutionScopeFixture,
  ): Promise<ExecutionScopeFixture> {
    const headers = { 'X-Agentstration-Context': base.scopes.instance };
    const profileUrl = `${consoleUrl}/api/modelprofiles/${profileName}?scopeRef=${encodeURIComponent(base.scopes.instance)}`;
    const currentProfile = await this.page.request.get(profileUrl, { headers });
    let profile: AgentWire;
    if (currentProfile.status() === 404) {
      const created = await this.page.request.post(`${consoleUrl}/api/modelprofiles`, {
        headers,
        data: {
          name: profileName,
          namespace: 'default',
          scopeRef: base.scopes.instance,
          properties: {
            displayName: 'Cross-scope usage profile',
            description: 'Deterministic Model Profile for cross-scope usage and navigation coverage.',
            provider: {
              name: 'execution-scope-ollama',
              namespace: 'default',
              scopeRef: base.scopes.instance,
            },
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
    await this.grantDescendantUse(consoleUrl, profile, base.scopes.tenant);

    const agentUrl = `${consoleUrl}/api/agents/${instanceAgentName}?scopeRef=${encodeURIComponent(base.scopes.instance)}`;
    const currentAgent = await this.page.request.get(agentUrl, { headers });
    if (currentAgent.status() === 404) {
      const created = await this.page.request.put(`${consoleUrl}/api/agents/${instanceAgentName}`, {
        headers: { ...headers, 'If-None-Match': '*' },
        data: {
          apiVersion: 'agentstration.io/v1',
          kind: 'Agent',
          scopeRef: base.scopes.instance,
          metadata: { name: instanceAgentName, namespace: 'default' },
          definition: {
            displayName: 'Instance model profile consumer',
            description: 'Deterministic Instance Agent for Model Profile usage coverage.',
            instructions: 'Return a concise deterministic response.',
            modelProfile: { name: profileName, namespace: 'default', scopeRef: base.scopes.instance },
            runtimeProfile: { name: 'maf-builtin', namespace: 'default', scopeRef: base.scopes.instance },
            tools: [], toolSets: [], behaviors: [], middleware: [], contextProviders: [], settings: {},
          },
        },
      });
      expect(created.status(), await created.text()).toBe(200);
    } else {
      expect(currentAgent.status(), await currentAgent.text()).toBe(200);
    }

    return { ...base, agentName: instanceAgentName, modelProfileName: profileName };
  }

  public async ensureWorkspaceAgent(
    consoleUrl: string,
    fixture: ExecutionScopeFixture,
    scopeRef = fixture.scopes.workspace,
    name = workspaceAgentName,
  ): Promise<void> {
    const headers = { 'X-Agentstration-Context': scopeRef };
    const url = `${consoleUrl}/api/agents/${encodeURIComponent(name)}?scopeRef=${encodeURIComponent(scopeRef)}`;
    const current = await this.page.request.get(url, { headers });
    if (current.status() === 200) return;
    expect(current.status(), await current.text()).toBe(404);

    const created = await this.page.request.put(`${consoleUrl}/api/agents/${encodeURIComponent(name)}`, {
      headers: { ...headers, 'If-None-Match': '*' },
      data: {
        apiVersion: 'agentstration.io/v1',
        kind: 'Agent',
        scopeRef,
        metadata: { name, namespace: 'default' },
        definition: {
          displayName: name === siblingAgentName
            ? 'Sibling model profile consumer'
            : 'Workspace model profile consumer',
          description: 'Deterministic Agent for cross-scope Model Profile usage coverage.',
          instructions: 'Return a concise deterministic response.',
          modelProfile: {
            name: fixture.modelProfileName,
            namespace: 'default',
            scopeRef: fixture.scopes.instance,
          },
          runtimeProfile: { name: 'maf-builtin', namespace: 'default', scopeRef: fixture.scopes.instance },
          tools: [], toolSets: [], behaviors: [], middleware: [], contextProviders: [], settings: {},
        },
      },
    });
    expect(created.status(), await created.text()).toBe(200);
  }

  public async resetWorkspaceAgents(consoleUrl: string, fixture: ExecutionScopeFixture): Promise<void> {
    await this.removeAgentIfPresent(consoleUrl, fixture.scopes.workspace, workspaceAgentName);
    await this.removeAgentIfPresent(
      consoleUrl,
      `/workspaces/${fixture.siblingWorkspaceId}`,
      siblingAgentName,
    );
  }

  public async deleteWorkspaceAgent(consoleUrl: string, fixture: ExecutionScopeFixture): Promise<void> {
    const scopeRef = fixture.scopes.workspace;
    const headers = { 'X-Agentstration-Context': scopeRef };
    const url = `${consoleUrl}/api/agents/${encodeURIComponent(workspaceAgentName)}?scopeRef=${encodeURIComponent(scopeRef)}`;
    const current = await this.page.request.get(url, { headers });
    expect(current.status(), await current.text()).toBe(200);
    const etag = current.headers()['etag'];
    expect(etag).toBeTruthy();
    const removed = await this.page.request.delete(url, { headers: { ...headers, 'If-Match': etag! } });
    expect(removed.status(), await removed.text()).toBe(204);
  }

  public async openUsages(consoleUrl: string, fixture: ExecutionScopeFixture): Promise<void> {
    const target = this.profileUsagesUrl(consoleUrl, fixture);
    const response = await this.page.goto(target, { waitUntil: 'domcontentloaded' });
    expect(response?.status(), target).toBe(200);
    await this.usagesPanel.waitFor({ state: 'visible' });
    await this.expectContext(fixture.scopes.instance);
  }

  public async assertUsages(expectedNames: string[], excludedNames: string[] = []): Promise<void> {
    const usages = this.page.getByTestId(TestIds.modelAdministration.profileUsage);
    await expect(usages).toHaveCount(expectedNames.length);
    for (const name of expectedNames) {
      await expect(usages.filter({ hasText: name })).toHaveCount(1);
    }
    for (const name of excludedNames) {
      await expect(usages.filter({ hasText: name })).toHaveCount(0);
    }
  }

  public async assertImpactCount(count: number): Promise<void> {
    const banner = this.page.getByTestId(TestIds.modelAdministration.profileImpactBanner);
    await expect(banner).toBeVisible();
    await expect(banner.locator('strong')).toContainText(count.toString());
  }

  public async openWorkspaceUsage(fixture: ExecutionScopeFixture): Promise<void> {
    await this.usageLink(workspaceAgentName).click();
    await this.expectContext(fixture.scopes.workspace);
    await this.page.getByTestId(TestIds.agentEditor.form).waitFor({ state: 'visible' });
    await expect(this.page).toHaveURL(new RegExp(`/agents/${workspaceAgentName}\\?`));
  }

  public async returnToProfileUsages(fixture: ExecutionScopeFixture): Promise<void> {
    await this.page.getByTestId(TestIds.agentEditor.contextReturn).click();
    await this.expectProfileUsages(fixture);
  }

  public async assertHistoryRoundTrip(fixture: ExecutionScopeFixture): Promise<void> {
    await this.page.goBack({ waitUntil: 'domcontentloaded' });
    await this.expectContext(fixture.scopes.workspace);
    await this.page.getByTestId(TestIds.agentEditor.form).waitFor({ state: 'visible' });
    await this.page.goForward({ waitUntil: 'domcontentloaded' });
    await this.expectProfileUsages(fixture);
  }

  public async assertReloadKeepsWorkspaceContext(fixture: ExecutionScopeFixture): Promise<void> {
    await this.page.reload({ waitUntil: 'domcontentloaded' });
    await this.expectContext(fixture.scopes.workspace);
    await this.page.getByTestId(TestIds.agentEditor.form).waitFor({ state: 'visible' });
  }

  public async assertNewTabIsolation(fixture: ExecutionScopeFixture): Promise<void> {
    const context = this.page.context();
    const [destination] = await Promise.all([
      context.waitForEvent('page'),
      this.usageLink(workspaceAgentName).click({ modifiers: ['Control'] }),
    ]);
    try {
      await destination.waitForLoadState('domcontentloaded');
      await expect(destination.getByTestId(TestIds.console.shell))
        .toHaveAttribute('data-context-scope', fixture.scopes.workspace);
      await destination.getByTestId(TestIds.agentEditor.form).waitFor({ state: 'visible' });
      await this.expectContext(fixture.scopes.instance);
      await expect(this.usagesPanel).toBeVisible();
    } finally {
      await destination.close();
    }
  }

  public async assertRestrictedVisibility(
    consoleUrl: string,
    fixture: ExecutionScopeFixture,
  ): Promise<void> {
    const suffix = Date.now().toString(36);
    const userName = `profile-usage-member-${suffix}`;
    const password = 'Profile-Usage-903!';
    const selection = await this.page.request.post(`${consoleUrl}/bff/identity/context/selection`, {
      data: { scopeRef: fixture.scopes.workspace },
    });
    expect(selection.status(), await selection.text()).toBe(200);
    const account = await this.page.request.post(`${consoleUrl}/api/identity/accounts/`, {
      data: {
        userName,
        password,
        displayName: 'Model profile usage member',
        email: null,
        workspaceId: fixture.workspaceId,
        role: 'Member',
      },
    });
    expect(account.status(), await account.text()).toBe(201);

    const browser = this.page.context().browser();
    if (!browser) throw new Error('The browser context is unavailable.');
    const memberContext = await browser.newContext({ locale: 'en-US', colorScheme: 'dark' });
    const memberPage = await memberContext.newPage();
    try {
      await new LoginPage(memberPage).signIn(consoleUrl, userName, password);
      const identityResponse = await memberPage.request.get(`${consoleUrl}/api/identity/context`);
      const identityBody = await identityResponse.text();
      expect(identityResponse.status(), identityBody).toBe(200);
      const identity = JSON.parse(identityBody) as IdentityContextWire;
      expect(identity.availableContexts.map(context => context.scopeRef), identityBody)
        .toEqual([fixture.scopes.workspace]);

      const target = this.profileUsagesUrl(consoleUrl, fixture, fixture.scopes.workspace);
      const profileResponse = await memberPage.goto(target, { waitUntil: 'domcontentloaded' });
      expect(profileResponse?.status(), target).toBe(200);
      const memberUsages = memberPage.getByTestId(TestIds.modelAdministration.profileUsage);
      await expect(memberUsages).toHaveCount(1);
      await expect(memberUsages.filter({ hasText: workspaceAgentName })).toHaveCount(1);
      await expect(memberUsages.filter({ hasText: fixture.agentName })).toHaveCount(0);
      await expect(memberUsages.filter({ hasText: siblingAgentName })).toHaveCount(0);

      const siblingScope = `/workspaces/${fixture.siblingWorkspaceId}`;
      const deniedTarget = new URL(`/agents/${encodeURIComponent(siblingAgentName)}`, consoleUrl);
      deniedTarget.searchParams.set('scopeRef', siblingScope);
      deniedTarget.searchParams.set('context', siblingScope);
      const denied = await memberPage.goto(deniedTarget.toString(), { waitUntil: 'domcontentloaded' });
      expect(denied?.status()).toBe(403);
      expect(await memberPage.locator('body').innerText()).not.toContain(siblingAgentName);
    } finally {
      await memberContext.close();
    }
  }

  public get primaryWorkspaceAgentName(): string { return workspaceAgentName; }
  public get hiddenSiblingAgentName(): string { return siblingAgentName; }

  private usageLink(name: string): Locator {
    return this.page.getByTestId(TestIds.modelAdministration.profileUsage).filter({ hasText: name }).locator('a');
  }

  private async removeAgentIfPresent(consoleUrl: string, scopeRef: string, name: string): Promise<void> {
    const headers = { 'X-Agentstration-Context': scopeRef };
    const url = `${consoleUrl}/api/agents/${encodeURIComponent(name)}?scopeRef=${encodeURIComponent(scopeRef)}`;
    const current = await this.page.request.get(url, { headers });
    if (current.status() === 404) return;
    expect(current.status(), await current.text()).toBe(200);
    const etag = current.headers()['etag'];
    expect(etag).toBeTruthy();
    const removed = await this.page.request.delete(url, { headers: { ...headers, 'If-Match': etag! } });
    expect(removed.status(), await removed.text()).toBe(204);
  }

  private async grantDescendantUse(
    consoleUrl: string,
    resource: AgentWire,
    tenantScope: string,
  ): Promise<void> {
    const headers = { 'X-Agentstration-Context': '/instance' };
    const url = `${consoleUrl}/api/resource-access-policies/${resource.uid}`;
    const current = await this.page.request.get(url, { headers });
    expect([200, 404]).toContain(current.status());
    const grants = current.status() === 404
      ? []
      : (await current.json() as {
        grants: Array<{ targetScopeRef: string; actions: string[]; includeDescendants: boolean }>;
      }).grants;
    if (grants.some(grant => grant.targetScopeRef === tenantScope
      && grant.includeDescendants
      && grant.actions.includes('discover')
      && grant.actions.includes('use'))) return;
    const saved = await this.page.request.put(url, {
      headers: {
        ...headers,
        ...(current.status() === 200 && current.headers()['etag'] ? { 'If-Match': current.headers()['etag']! } : {}),
      },
      data: {
        resource: {
          uid: resource.uid,
          ownerScopeRef: '/instance',
          namespace: resource.namespace,
          kind: resource.kind,
          generation: resource.generation,
        },
        grants: [
          ...grants.filter(grant => grant.targetScopeRef !== tenantScope),
          { targetScopeRef: tenantScope, actions: ['discover', 'use'], includeDescendants: true },
        ],
      },
    });
    expect(saved.status(), await saved.text()).toBe(200);
  }

  private profileUsagesUrl(
    consoleUrl: string,
    fixture: ExecutionScopeFixture,
    contextScope = fixture.scopes.instance,
  ): string {
    const target = new URL(`/modelprofiles/${encodeURIComponent(fixture.modelProfileName)}`, consoleUrl);
    target.searchParams.set('scopeRef', fixture.scopes.instance);
    target.searchParams.set('context', contextScope);
    target.searchParams.set('tab', 'usages');
    return target.toString();
  }

  private async expectProfileUsages(fixture: ExecutionScopeFixture): Promise<void> {
    await this.expectContext(fixture.scopes.instance);
    await expect(this.page).toHaveURL(/\/modelprofiles\/[^?]+\?.*tab=usages/);
    await this.usagesPanel.waitFor({ state: 'visible' });
  }

  private async expectContext(scopeRef: string): Promise<void> {
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-context-scope', scopeRef);
    await expect(this.page).toHaveURL(new RegExp(`context=${encodeURIComponent(scopeRef)}`));
  }
}
