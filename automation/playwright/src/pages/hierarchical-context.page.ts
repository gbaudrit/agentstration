import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import type { ExpectedText } from '../locales/expected-text.js';
import { LoginPage } from './login.page.js';

export interface HierarchicalContextFixture {
  agentName: string;
  agentUid: string;
  agentGeneration: number;
  tenantId: string;
  workspaceId: string;
  scopes: { instance: string; tenant: string; workspace: string };
}

interface AccessPolicyWire {
  resource: {
    uid: string;
    ownerScopeRef: string;
    namespace: string;
    kind: string;
    generation: number;
  };
  grants: Array<{
    targetScopeRef: string;
    actions: string[];
    includeDescendants: boolean;
  }>;
}

interface IdentityContextWire {
  availableContexts: Array<{
    scopeRef: string;
    kind: number;
  }>;
}

export class HierarchicalContextPage {
  public constructor(private readonly page: Page) {}

  public get selector(): Locator { return this.page.getByTestId(TestIds.console.contextSelector); }
  public get shell(): Locator { return this.page.getByTestId(TestIds.console.shell); }
  public get agents(): Locator { return this.page.getByTestId(TestIds.resourceAdministration.agents); }

  public async openSelector(): Promise<void> {
    await expect(this.shell).toHaveAttribute('data-preferences-ready', 'true');
    const trigger = this.page.getByTestId(TestIds.console.contextSelectorTrigger);
    await expect(async () => {
      if (await this.selector.count() === 0) {
        await trigger.focus();
        await trigger.press('Enter');
      }
      await expect(this.selector).toBeVisible();
    }).toPass({ timeout: 30_000 });
  }

  public async assertHierarchy(fixture: HierarchicalContextFixture): Promise<void> {
    await this.openSelector();
    await expect(this.selector.getByTestId(TestIds.console.contextOptionInstance)).toBeVisible();
    const tenant = this.tenantOption(fixture.tenantId);
    const workspace = this.workspaceOption(fixture.workspaceId);
    await expect(tenant).toBeVisible();
    await expect(workspace).toBeVisible();
    await expect(workspace.locator('xpath=ancestor::div[contains(@class,"context-tenant-workspaces")]'))
      .toHaveCount(1);
    await expect(workspace.locator('xpath=ancestor::section[contains(@class,"context-tenant-group")]'))
      .toHaveAttribute('data-tenant-id', fixture.tenantId);
  }

  public async selectInstance(): Promise<void> {
    if (await this.shell.getAttribute('data-context-kind') === 'Instance') return;
    await this.openSelector();
    await this.selector.getByTestId(TestIds.console.contextOptionInstance).click();
    await expect(this.shell).toHaveAttribute('data-context-kind', 'Instance');
    await expect(this.page).toHaveURL(/context=%2Finstance/);
  }

  public async selectTenant(tenantId: string): Promise<void> {
    const scopeRef = `/tenants/${tenantId}`;
    if (await this.shell.getAttribute('data-context-scope') === scopeRef) return;
    await this.openSelector();
    await this.tenantOption(tenantId).click();
    await expect(this.shell).toHaveAttribute('data-context-scope', scopeRef);
  }

  public async selectWorkspaceWithKeyboard(workspaceId: string, search: string): Promise<void> {
    await this.openSelector();
    const input = this.page.getByTestId(TestIds.console.contextSearch);
    await input.fill(search);
    const option = this.selector.getByTestId(TestIds.console.contextOptionWorkspace).first();
    await expect(option).toBeVisible();
    await expect(this.selector.getByTestId(TestIds.console.contextOptionWorkspace)).toHaveCount(1);
    await option.focus();
    await option.press('Enter');
    await expect(this.shell).toHaveAttribute('data-context-kind', 'Workspace');
    await expect(this.shell).toHaveAttribute('data-workspace-id', workspaceId);
  }

  public async assertContextSurvivesNavigation(consoleUrl: string, scopeRef: string): Promise<void> {
    await this.page.goto(this.contextUrl(`${consoleUrl}/agents`, scopeRef), { waitUntil: 'domcontentloaded' });
    await this.agents.waitFor({ state: 'visible' });
    await expect(this.shell).toHaveAttribute('data-context-scope', scopeRef);
    await expect(this.page).toHaveURL(new RegExp(`context=${encodeURIComponent(scopeRef)}`));
    await this.page.reload({ waitUntil: 'domcontentloaded' });
    await expect(this.shell).toHaveAttribute('data-context-scope', scopeRef);
    await this.page.goto(this.contextUrl(`${consoleUrl}/flows`, scopeRef), { waitUntil: 'domcontentloaded' });
    await this.page.goBack({ waitUntil: 'domcontentloaded' });
    await expect(this.shell).toHaveAttribute('data-context-scope', scopeRef);
  }

  public async openAgents(consoleUrl: string): Promise<void> {
    const scopeRef = await this.shell.getAttribute('data-context-scope');
    const response = await this.page.goto(
      this.contextUrl(`${consoleUrl}/agents`, scopeRef),
      { waitUntil: 'domcontentloaded' },
    );
    expect(response?.status()).toBe(200);
    await this.agents.waitFor({ state: 'visible' });
  }

  public async expectAgentVisible(agentName: string, visible: boolean): Promise<void> {
    const link = this.page.locator(`a.resource-name-link[href^="/agents/${agentName}?"]`).first();
    if (visible) await expect(link).toBeVisible();
    else await expect(link).toHaveCount(0);
  }

  public async setAgentAccess(
    consoleUrl: string,
    fixture: HierarchicalContextFixture,
    actions: Array<'discover' | 'use'>,
  ): Promise<void> {
    const url = `${consoleUrl}/api/resource-access-policies/${fixture.agentUid}`;
    const headers = { 'X-Agentstration-Context': fixture.scopes.instance };
    const current = await this.page.request.get(url, { headers });
    expect([200, 404]).toContain(current.status());
    const etag = current.status() === 200 ? current.headers()['etag'] : undefined;
    const policy: AccessPolicyWire = {
      resource: {
        uid: fixture.agentUid,
        ownerScopeRef: fixture.scopes.instance,
        namespace: 'default',
        kind: 'Agent',
        generation: fixture.agentGeneration,
      },
      grants: actions.length === 0 ? [] : [{
        targetScopeRef: fixture.scopes.workspace,
        actions,
        includeDescendants: false,
      }],
    };
    const saved = await this.page.request.put(url, {
      headers: { ...headers, ...(etag ? { 'If-Match': etag } : {}) },
      data: policy,
    });
    expect(saved.status(), await saved.text()).toBe(200);
  }

  public async expectAgentUseDenied(consoleUrl: string, fixture: HierarchicalContextFixture): Promise<void> {
    const response = await this.page.request.post(`${consoleUrl}/api/runtime/runs`, {
      headers: { 'X-Agentstration-Context': fixture.scopes.workspace },
      data: this.runtimeRequest(fixture),
    });
    const body = await response.text();
    expect(response.status(), body).toBe(400);
    expect(body).toContain('agent_not_found');
  }

  public async expectAgentUseAccepted(consoleUrl: string, fixture: HierarchicalContextFixture): Promise<void> {
    const response = await this.page.request.post(`${consoleUrl}/api/runtime/runs`, {
      headers: { 'X-Agentstration-Context': fixture.scopes.workspace },
      data: this.runtimeRequest(fixture),
    });
    expect(response.status(), await response.text()).toBe(202);
  }

  public async assertDirtyContextGuard(
    consoleUrl: string,
    fixture: HierarchicalContextFixture,
    expectedMessage: string,
  ): Promise<void> {
    await this.selectInstance();
    const response = await this.page.goto(
      this.contextUrl(
        `${consoleUrl}/agents/${encodeURIComponent(fixture.agentName)}?scopeRef=${encodeURIComponent(fixture.scopes.instance)}`,
        fixture.scopes.instance,
      ),
      { waitUntil: 'domcontentloaded' },
    );
    expect(response?.status()).toBe(200);
    await this.page.locator(
      `[data-testid="${TestIds.agentEditor.form}"][data-interactive="true"]`,
    ).waitFor({ state: 'visible' });
    const description = this.page.getByTestId(TestIds.agentEditor.description);
    await description.fill(`Unsaved context guard ${Date.now()}`);
    await description.blur();
    await expect(this.page.locator('.unsaved-changes-bar')).toBeVisible();

    let dialogMessage = '';
    this.page.once('dialog', async dialog => {
      dialogMessage = dialog.message();
      await dialog.dismiss();
    });
    await this.openSelector();
    await this.workspaceOption(fixture.workspaceId).click();
    await expect.poll(() => dialogMessage).toBe(expectedMessage);
    await expect(this.shell).toHaveAttribute('data-context-kind', 'Instance');
    await this.page.reload({ waitUntil: 'domcontentloaded' });
  }

  public async assertRestrictedMember(
    consoleUrl: string,
    fixture: HierarchicalContextFixture,
  ): Promise<void> {
    const workspaceSelection = await this.page.request.post(`${consoleUrl}/bff/identity/context/selection`, {
      data: { scopeRef: fixture.scopes.workspace },
    });
    expect(workspaceSelection.status(), await workspaceSelection.text()).toBe(200);

    const suffix = Date.now().toString(36);
    const userName = `context-member-${suffix}`;
    const password = 'Context-Member-860!';
    const account = await this.page.request.post(`${consoleUrl}/api/identity/accounts/`, {
      data: {
        userName,
        password,
        displayName: 'Hierarchical context member',
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

      const memberShell = memberPage.getByTestId(TestIds.console.shell);
      await expect(memberShell).toHaveAttribute('data-context-kind', 'Workspace');
      await expect(memberShell).toHaveAttribute('data-preferences-ready', 'true');
      await memberPage.getByTestId(TestIds.console.contextSelectorTrigger).click();
      const selector = memberPage.getByTestId(TestIds.console.contextSelector);
      await expect(selector.getByTestId(TestIds.console.contextOptionInstance)).toHaveCount(0);
      await expect(selector.getByTestId(TestIds.console.contextOptionTenant)).toHaveCount(0);
      await expect(selector.getByTestId(TestIds.console.contextOptionWorkspace)).toHaveCount(1);

      const denied = await memberPage.request.post(`${consoleUrl}/bff/identity/context/selection`, {
        data: { scopeRef: fixture.scopes.instance },
      });
      const deniedBody = await denied.text();
      expect(denied.status(), deniedBody).toBe(403);
      expect(deniedBody).not.toContain(fixture.agentName);

      const direct = await memberPage.goto(
        `${consoleUrl}/?context=${encodeURIComponent(fixture.scopes.instance)}`,
        { waitUntil: 'domcontentloaded' },
      );
      expect(direct?.status()).toBe(403);
      const workspace = await memberPage.goto(
        `${consoleUrl}/?context=${encodeURIComponent(fixture.scopes.workspace)}`,
        { waitUntil: 'domcontentloaded' },
      );
      expect(workspace?.status()).toBe(200);
      await expect(memberShell).toHaveAttribute('data-context-kind', 'Workspace');
      await expect(memberShell).toHaveAttribute('data-workspace-id', fixture.workspaceId);
    } finally {
      await memberContext.close();
    }
  }

  public async assertLocalized(expected: ExpectedText['contextSelector']): Promise<void> {
    const trigger = this.page.getByTestId(TestIds.console.contextSelectorTrigger);
    await expect(trigger).toHaveAttribute('aria-label', expected.change);
    await this.openSelector();
    await expect(this.selector).toHaveAttribute('aria-label', expected.choose);
    await expect(this.page.getByTestId(TestIds.console.contextSearch)).toHaveAttribute('placeholder', expected.search);
    await expect(this.selector.getByText(expected.globalAdministration, { exact: true })).toBeVisible();
    await expect(this.selector.getByText(expected.tenantAdministration, { exact: true }).first()).toBeVisible();
    await expect(this.selector.locator('.context-kind').filter({ hasText: expected.instance }).first()).toBeVisible();
    await expect(this.selector.locator('.context-kind').filter({ hasText: expected.tenant }).first()).toBeVisible();
    await expect(this.selector.locator('.context-kind').filter({ hasText: expected.workspace }).first()).toBeVisible();
    await this.selector.getByRole('button', { name: expected.close }).click();
  }

  public async assertResponsiveHierarchy(): Promise<void> {
    await this.openSelector();
    const selectorBox = await this.selector.boundingBox();
    const viewport = this.page.viewportSize();
    expect(selectorBox).not.toBeNull();
    expect(viewport).not.toBeNull();
    expect(selectorBox!.x).toBeGreaterThanOrEqual(0);
    expect(selectorBox!.x + selectorBox!.width).toBeLessThanOrEqual(viewport!.width);
    await expect(this.selector.getByTestId(TestIds.console.contextOptionInstance)).toBeVisible();
    await expect(this.selector.getByTestId(TestIds.console.contextOptionTenant).first()).toBeVisible();
    await expect(this.selector.getByTestId(TestIds.console.contextOptionWorkspace).first()).toBeVisible();
  }

  private tenantOption(tenantId: string): Locator {
    return this.selector.locator(
      `[data-testid="${TestIds.console.contextOptionTenant}"][data-context-tenant-id="${tenantId}"]`,
    );
  }

  private workspaceOption(workspaceId: string): Locator {
    return this.selector.locator(
      `[data-testid="${TestIds.console.contextOptionWorkspace}"][data-context-workspace-id="${workspaceId}"]`,
    );
  }

  private contextUrl(url: string, scopeRef: string | null): string {
    if (!scopeRef) return url;
    const contextualUrl = new URL(url);
    contextualUrl.searchParams.set('context', scopeRef);
    return contextualUrl.toString();
  }

  private runtimeRequest(fixture: HierarchicalContextFixture): object {
    return {
      executionScopeRef: fixture.scopes.workspace,
      agent: {
        resourceId: fixture.agentName,
        version: fixture.agentGeneration,
        namespace: 'default',
        scopeRef: fixture.scopes.instance,
        resourceUid: fixture.agentUid,
      },
      input: { messages: [{ role: 'User', content: 'Validate hierarchical context access.' }] },
      origin: 'Api',
    };
  }
}
