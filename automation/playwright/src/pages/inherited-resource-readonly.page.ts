import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import type { ExpectedText } from '../locales/expected-text.js';
import type { ExecutionScopeFixture } from './execution-scopes.page.js';

interface ResourceIdentityWire {
  uid: string;
  generation: number;
  namespace: string;
  kind: string;
}

interface AccessPolicyWire {
  grants: Array<{ targetScopeRef: string; actions: string[]; includeDescendants: boolean }>;
}

export interface InheritedResourceReadOnlyFixture {
  tenantAgentName: string;
  tenantAgent: ResourceIdentityWire;
}

const tenantAgentName = 'tenant-inherited-readonly-agent';

export class InheritedResourceReadOnlyPage {
  public constructor(private readonly page: Page) {}

  public get notice(): Locator { return this.page.getByTestId(TestIds.common.inheritedResourceNotice); }

  public async ensureTenantAgent(
    consoleUrl: string,
    fixture: ExecutionScopeFixture,
  ): Promise<InheritedResourceReadOnlyFixture> {
    const headers = { 'X-Agentstration-Context': fixture.scopes.tenant };
    const exactUrl = `${consoleUrl}/api/agents/${tenantAgentName}?scopeRef=${encodeURIComponent(fixture.scopes.tenant)}`;
    const current = await this.page.request.get(exactUrl, { headers });
    let agent: ResourceIdentityWire;
    if (current.status() === 404) {
      const created = await this.page.request.put(`${consoleUrl}/api/agents/${tenantAgentName}`, {
        headers: { ...headers, 'If-None-Match': '*' },
        data: {
          apiVersion: 'agentstration.io/v1',
          kind: 'Agent',
          scopeRef: fixture.scopes.tenant,
          metadata: { name: tenantAgentName, namespace: 'default' },
          definition: {
            displayName: 'Tenant inherited read-only Agent',
            description: 'Deterministic Tenant Agent for inherited-resource browser coverage.',
            instructions: 'Return a concise deterministic response.',
            modelProfile: { name: fixture.modelProfileName, namespace: 'default', scopeRef: fixture.scopes.instance },
            runtimeProfile: { name: 'maf-builtin', namespace: 'default', scopeRef: fixture.scopes.instance },
            tools: [], toolSets: [], behaviors: [], middleware: [], contextProviders: [], settings: {},
          },
        },
      });
      expect(created.status(), await created.text()).toBe(200);
      agent = await created.json() as ResourceIdentityWire;
    } else {
      expect(current.status(), await current.text()).toBe(200);
      agent = await current.json() as ResourceIdentityWire;
    }

    await this.grantWorkspaceUse(consoleUrl, fixture.scopes.tenant, fixture.scopes.workspace, agent);
    return { tenantAgentName, tenantAgent: agent };
  }

  public async openAgent(consoleUrl: string, name: string, ownerScope: string, contextScope: string): Promise<void> {
    await this.open(
      consoleUrl,
      `/agents/${encodeURIComponent(name)}`,
      ownerScope,
      contextScope,
      TestIds.agentEditor.form,
    );
  }

  public async openModelProfile(
    consoleUrl: string,
    name: string,
    ownerScope: string,
    contextScope: string,
  ): Promise<void> {
    await this.open(
      consoleUrl,
      `/modelprofiles/${encodeURIComponent(name)}`,
      ownerScope,
      contextScope,
      TestIds.modelAdministration.profileForm,
    );
  }

  public async assertInheritedNotice(
    expected: ExpectedText['inheritedResource'],
    ownerLabel: 'Instance' | 'Tenant',
  ): Promise<void> {
    await expect(this.notice).toBeVisible();
    await expect(this.notice.getByText(expected.title, { exact: true })).toBeVisible();
    await expect(this.notice.getByText(expected.message, { exact: true })).toBeVisible();
    await expect(this.notice.getByText(ownerLabel, { exact: true })).toBeVisible();
    const action = this.notice.getByTestId(TestIds.common.switchToOwnerContext);
    await expect(action).toHaveText(expected.changeScope);
    const ownerScope = ownerLabel === 'Instance' ? '/instance' : /\/tenants\/[0-9a-f-]+/i;
    const href = await action.getAttribute('href');
    expect(href).toBeTruthy();
    const context = new URL(href!, this.page.url()).searchParams.get('context');
    if (typeof ownerScope === 'string') expect(context).toBe(ownerScope);
    else expect(context).toMatch(ownerScope);

    const tabs = this.page.locator('.resource-tabs').first();
    await expect(tabs).toBeVisible();
    expect(await this.notice.evaluate((notice, tabsElement) =>
      Boolean(notice.compareDocumentPosition(tabsElement) & Node.DOCUMENT_POSITION_FOLLOWING),
    await tabs.elementHandle())).toBe(true);
  }

  public async assertAgentReadOnly(): Promise<void> {
    await expect(this.page.getByTestId(TestIds.agentEditor.identitySection).locator('input').first()).toBeDisabled();
    await expect(this.page.getByTestId(TestIds.agentEditor.saveDraft)).toHaveCount(0);
    await expect(this.page.getByTestId(TestIds.agentEditor.accessTab)).toHaveCount(0);
  }

  public async assertModelProfileReadOnly(): Promise<void> {
    await expect(this.page.getByTestId(TestIds.modelAdministration.profileDisplayName)).toBeDisabled();
    await expect(this.page.getByTestId(TestIds.modelAdministration.profileSave)).toHaveCount(0);
    await expect(this.page.getByTestId(TestIds.modelAdministration.profileDelete)).toHaveCount(0);
    await expect(this.page.getByTestId(TestIds.modelAdministration.profileAccessTab)).toHaveCount(0);
    await expect(this.page.getByTestId(TestIds.modelAdministration.profileImpactBanner)).toHaveCount(0);
  }

  public async assertAgentManageable(ownerScope: string): Promise<void> {
    await this.expectContext(ownerScope);
    await expect(this.notice).toHaveCount(0);
    await expect(this.page.getByTestId(TestIds.agentEditor.identitySection).locator('input').first()).toBeEnabled();
    await expect(this.page.getByTestId(TestIds.agentEditor.accessTab)).toBeVisible();
  }

  public async assertModelProfileManageable(): Promise<void> {
    await this.expectContext('/instance');
    await expect(this.notice).toHaveCount(0);
    await expect(this.page.getByTestId(TestIds.modelAdministration.profileDisplayName)).toBeEnabled();
    await expect(this.page.getByTestId(TestIds.modelAdministration.profileSave)).toBeVisible();
    await expect(this.page.getByTestId(TestIds.modelAdministration.profileDelete)).toBeVisible();
    await expect(this.page.getByTestId(TestIds.modelAdministration.profileAccessTab)).toBeVisible();
  }

  public async changeToOwnerScope(): Promise<void> {
    await this.notice.getByTestId(TestIds.common.switchToOwnerContext).click();
  }

  public async assertModelProfileSelectable(
    consoleUrl: string,
    profileName: string,
    workspaceScope: string,
  ): Promise<void> {
    const target = new URL('/agents/new', consoleUrl);
    target.searchParams.set('context', workspaceScope);
    const response = await this.page.goto(target.toString(), { waitUntil: 'domcontentloaded' });
    expect(response?.status()).toBe(200);
    await this.page.locator(`[data-testid="${TestIds.agentEditor.form}"][data-interactive="true"]`)
      .waitFor({ state: 'visible' });
    await expect(this.page.getByTestId(TestIds.agentEditor.modelProfile)
      .locator('option').filter({ hasText: profileName })).toHaveCount(1);
  }

  public async assertDescendantMutationRejected(
    consoleUrl: string,
    profileName: string,
    workspaceScope: string,
  ): Promise<void> {
    const url = `${consoleUrl}/api/modelprofiles/${encodeURIComponent(profileName)}`
      + `?resourceNamespace=default&scopeRef=${encodeURIComponent('/instance')}`;
    const headers = { 'X-Agentstration-Context': workspaceScope };
    const current = await this.page.request.get(url, { headers });
    const body = await current.text();
    expect(current.status(), body).toBe(200);
    const etag = current.headers()['etag'];
    expect(etag).toBeTruthy();
    const mutation = await this.page.request.delete(url, {
      headers: { ...headers, 'If-Match': etag! },
    });
    expect(mutation.status(), await mutation.text()).toBe(403);
  }

  private async grantWorkspaceUse(
    consoleUrl: string,
    ownerScope: string,
    workspaceScope: string,
    resource: ResourceIdentityWire,
  ): Promise<void> {
    const url = `${consoleUrl}/api/resource-access-policies/${resource.uid}`;
    const headers = { 'X-Agentstration-Context': ownerScope };
    const current = await this.page.request.get(url, { headers });
    expect([200, 404]).toContain(current.status());
    const policy = current.status() === 200 ? await current.json() as AccessPolicyWire : { grants: [] };
    if (policy.grants.some(grant => grant.targetScopeRef === workspaceScope
      && grant.actions.includes('discover') && grant.actions.includes('use'))) return;
    const saved = await this.page.request.put(url, {
      headers: {
        ...headers,
        ...(current.status() === 200 && current.headers()['etag'] ? { 'If-Match': current.headers()['etag']! } : {}),
      },
      data: {
        resource: {
          uid: resource.uid, ownerScopeRef: ownerScope, namespace: resource.namespace,
          kind: resource.kind, generation: resource.generation,
        },
        grants: [
          ...policy.grants.filter(grant => grant.targetScopeRef !== workspaceScope),
          { targetScopeRef: workspaceScope, actions: ['discover', 'use'], includeDescendants: false },
        ],
      },
    });
    expect(saved.status(), await saved.text()).toBe(200);
  }

  private async open(
    consoleUrl: string,
    path: string,
    ownerScope: string,
    contextScope: string,
    marker: string,
  ): Promise<void> {
    const target = new URL(path, consoleUrl);
    target.searchParams.set('scopeRef', ownerScope);
    target.searchParams.set('context', contextScope);
    const response = await this.page.goto(target.toString(), { waitUntil: 'domcontentloaded' });
    expect(response?.status(), target.toString()).toBe(200);
    await this.page.getByTestId(marker).waitFor({ state: 'visible' });
    await this.expectContext(contextScope);
  }

  private async expectContext(scopeRef: string): Promise<void> {
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-context-scope', scopeRef);
    await expect(this.page).toHaveURL(new RegExp(`context=${encodeURIComponent(scopeRef)}`));
  }
}
