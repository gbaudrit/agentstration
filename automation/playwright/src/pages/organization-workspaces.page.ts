import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { OrganizationStructureTextByLocale, type SupportedTestLocale } from '../locales/expected-text.js';
import { fillAndCommit } from './controls.js';

export interface WorkspaceDefinition {
  name: string;
  displayName: string;
}

export class OrganizationWorkspacesPage {
  public constructor(private readonly page: Page) {}

  public async open(consoleUrl: string): Promise<void> {
    const target = new URL('/settings/organization/workspaces', consoleUrl);
    const scopeRef = await this.page.getByTestId(TestIds.console.shell)
      .getAttribute('data-context-scope')
      .catch(() => null);
    if (scopeRef) target.searchParams.set('context', scopeRef);
    const response = await this.page.goto(target.toString(), { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Organization workspaces page returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.locator(`[data-testid="${TestIds.organizationWorkspaces.structure}"][data-interactive="true"]`).waitFor({ state: 'visible' });
    await this.page.getByTestId(TestIds.organizationWorkspaces.openCreate).click();
    await this.page.getByTestId(TestIds.organizationWorkspaces.createForm).waitFor({ state: 'visible' });
  }


  public async openStructure(consoleUrl: string, scope: string, legacy = false): Promise<void> {
    const target = new URL(legacy ? '/settings/organization/workspaces' : '/settings/organization/structure', consoleUrl);
    target.searchParams.set('context', scope);
    const response = await this.page.goto(target.toString(), { waitUntil: 'domcontentloaded' });
    expect(response?.ok()).toBeTruthy();
    await expect(this.structure).toHaveAttribute('data-interactive', 'true');
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-context-scope', scope);
  }

  private tenant(name: string): Locator {
    return this.page.locator(`[data-testid="${TestIds.organizationWorkspaces.tenantRow}"][data-tenant-name="${name}"]`);
  }

  public async inspectInstance(locale: SupportedTestLocale): Promise<void> {
    await expect(this.page.getByRole('heading', { name: OrganizationStructureTextByLocale[locale].title, exact: true })).toBeVisible();
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.openTenantCreate)).toBeVisible();
    await expect(this.structure.locator('[aria-current="true"]')).toHaveCount(0);
    const inventory = await this.page.request.get(new URL('/api/identity/organization/structure', this.page.url()).toString(), { headers: { 'X-Agentstration-Context': '/instance' } });
    expect(inventory.ok()).toBeTruthy();
    const body = await inventory.json();
    expect(body.tenants.length).toBeGreaterThan(0);
    for (const entry of body.tenants) {
      expect(['agentstration-system']).not.toContain(entry.tenant.name);
      expect(entry.workspaces.some((workspace: { name: string }) => ['agentstration-instance-operations', 'agentstration-tenant-operations'].includes(workspace.name))).toBeFalsy();
    }
    await this.assertWithinViewport();
  }

  public async beginTenant(locale: SupportedTestLocale): Promise<void> {
    await this.page.getByTestId(TestIds.organizationWorkspaces.openTenantCreate).click();
    await expect(this.createForm.locator('h2')).toBeFocused();
    await expect(this.createForm.locator('.parent-context')).toContainText(OrganizationStructureTextByLocale[locale].parentInstance);
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.create)).toBeDisabled();
  }

  public async submitTenant(definition: WorkspaceDefinition): Promise<string> {
    await this.fillIdentity(definition);
    await this.page.getByTestId(TestIds.organizationWorkspaces.technicalName).press('Enter');
    const row = this.tenant(definition.name);
    await expect(row).toBeVisible();
    await expect(this.createForm).toHaveCount(0);
    const id = await row.getAttribute('data-tenant-id');
    if (!id) throw new Error('Created Tenant identifier is missing.');
    return id;
  }

  public async expandTenant(name: string, locale: SupportedTestLocale): Promise<void> {
    const row = this.tenant(name);
    const toggle = row.getByRole('button', { expanded: true });
    if (await toggle.count()) await toggle.click();
    await expect(row.locator('.tenant-toggle')).toHaveAttribute('aria-expanded', 'false');
    await row.getByRole('button', { expanded: false }).click();
    await expect(row.locator('.tenant-toggle')).toHaveAttribute('aria-expanded', 'true');
    await expect(row.getByText(OrganizationStructureTextByLocale[locale].empty, { exact: true })).toBeVisible();
  }

  public async beginWorkspace(tenant: WorkspaceDefinition, locale: SupportedTestLocale): Promise<void> {
    await this.tenant(tenant.name).getByTestId(TestIds.organizationWorkspaces.openCreate).click();
    await expect(this.createForm.locator('h2')).toBeFocused();
    await expect(this.createForm.locator('.parent-context')).toContainText(OrganizationStructureTextByLocale[locale].parentTenant);
    await expect(this.createForm.locator('.parent-context strong')).toHaveText(tenant.displayName);
    await expect(this.createForm.locator('.parent-context small')).toHaveText(tenant.name);
    await this.assertWithinViewport();
  }

  public async submitWorkspace(definition: WorkspaceDefinition): Promise<string> {
    await this.fillIdentity(definition);
    await this.page.getByTestId(TestIds.organizationWorkspaces.technicalName).press('Enter');
    await expect(this.row(definition.name)).toBeVisible();
    await expect(this.createForm).toHaveCount(0);
    const id = await this.row(definition.name).getAttribute('data-workspace-id');
    if (!id) throw new Error('Created Workspace identifier is missing.');
    return id;
  }

  public async assertWorkspaceParent(tenantName: string, workspaceName: string): Promise<void> {
    await expect(this.tenant(tenantName).getByTestId(TestIds.organizationWorkspaces.row)
      .filter({ hasText: workspaceName })).toHaveCount(1);
  }

  public async searchStructure(name: string, locale: SupportedTestLocale): Promise<void> {
    await this.page.getByRole('searchbox', { name: OrganizationStructureTextByLocale[locale].search }).fill(name);
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.tenantRow)).toHaveCount(1);
    await expect(this.row(name)).toBeVisible();
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.row)).toHaveCount(1);
  }

  public async openTenantLink(definition: WorkspaceDefinition, id: string, locale: SupportedTestLocale): Promise<void> {
    const link = this.tenant(definition.name).getByRole('link', { name: OrganizationStructureTextByLocale[locale].openTenant(definition.displayName), exact: true });
    await expect(link).toHaveAttribute('href', `/settings/organization/structure?context=${encodeURIComponent(`/tenants/${id}`)}`);
    await Promise.all([this.waitForContextNavigation(`/tenants/${id}`), link.click()]);
    await this.assertCurrent(this.tenant(definition.name).locator('.tenant-heading'), locale);
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.tenantRow)).toHaveCount(1);
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.openTenantCreate)).toHaveCount(0);
  }

  public async openWorkspaceLink(definition: WorkspaceDefinition, id: string, locale: SupportedTestLocale): Promise<void> {
    const link = this.row(definition.name).getByRole('link', { name: OrganizationStructureTextByLocale[locale].openWorkspace(definition.displayName), exact: true });
    await expect(link).toHaveAttribute('href', `/settings/organization/structure?context=${encodeURIComponent(`/workspaces/${id}`)}`);
    await Promise.all([this.waitForContextNavigation(`/workspaces/${id}`), link.click()]);
    await this.assertWorkspaceContext(definition.name, locale);
  }

  public async assertWorkspaceContext(name: string, locale: SupportedTestLocale): Promise<void> {
    await this.assertCurrent(this.row(name), locale);
    await expect(this.structure.locator('.tenant-heading[aria-current], .tenant-heading .current-context')).toHaveCount(0);
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.row)).toHaveCount(1);
    await this.assertWithinViewport();
  }

  public async assertTenantContext(name: string, locale: SupportedTestLocale): Promise<void> {
    await this.assertCurrent(this.tenant(name).locator('.tenant-heading'), locale);
    await this.assertWithinViewport();
  }

  private async assertCurrent(row: Locator, locale: SupportedTestLocale): Promise<void> {
    await expect(this.structure.locator('.current-context[aria-current="true"]')).toHaveCount(1);
    await expect(row).toHaveClass(/is-current/);
    const marker = row.locator('.current-context[aria-current="true"]');
    await expect(marker).toHaveText(OrganizationStructureTextByLocale[locale].current);
    await expect(marker.locator('svg')).toBeVisible();
    await expect(row.getByRole('link')).toHaveCount(0);
  }

  public async assertWithinViewport(): Promise<void> {
    await expect.poll(() => this.page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBeTruthy();
    const bounds = await this.structure.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.x).toBeGreaterThanOrEqual(0);
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(this.page.viewportSize()!.width + 1);
  }

  public async inspectNameFailures(definition: WorkspaceDefinition, kind: 'tenant' | 'workspace', locale: SupportedTestLocale): Promise<void> {
    const submit = this.page.getByTestId(TestIds.organizationWorkspaces.create);
    await expect(submit).toBeDisabled();
    await this.fillIdentity({ displayName: definition.displayName, name: 'invalid_name' });
    await expect(submit).toBeDisabled();
    await this.fillIdentity(definition);
    await submit.click();
    await expect(this.createForm.getByRole('alert')).toHaveText(OrganizationStructureTextByLocale[locale][kind === 'tenant' ? 'tenantConflict' : 'workspaceConflict']);
    await expect(this.createForm).toBeVisible();
    await this.fillIdentity({ displayName: 'Reserved topology', name: kind === 'tenant' ? 'agentstration-system' : 'agentstration-tenant-operations' });
    await submit.click();
    await expect(this.createForm.getByRole('alert')).toHaveText(OrganizationStructureTextByLocale[locale].invalid);
  }

  public async dismissCreation(): Promise<void> {
    await this.page.getByTestId(TestIds.organizationWorkspaces.technicalName).press('Escape');
    await expect(this.createForm).toHaveCount(0);
    await expect(this.page.getByRole('searchbox')).toBeFocused();
  }

  public async inspectDeniedOperations(consoleUrl: string, ownScope: string, foreignTenantId: string): Promise<void> {
    const headers = { 'X-Agentstration-Context': ownScope };
    const tenant = await this.page.request.post(`${consoleUrl}/api/identity/tenants`, { headers, data: { name: 'forbidden-tenant', displayName: 'Forbidden Tenant' } });
    expect(tenant.status()).toBe(403);
    const workspace = await this.page.request.post(`${consoleUrl}/api/identity/organization/workspaces`, {
      headers, data: { tenantId: foreignTenantId, name: 'forbidden-workspace', displayName: 'Forbidden Workspace' },
    });
    expect(workspace.status()).toBe(403);
    const inventory = await this.page.request.get(`${consoleUrl}/api/identity/organization/structure`, { headers: { 'X-Agentstration-Context': `/tenants/${foreignTenantId}` } });
    expect(inventory.status()).toBe(403);
  }

  public async inspectViewer(consoleUrl: string, scope: string, foreignTenantId: string): Promise<void> {
    await this.openStructure(consoleUrl, scope);
    const inventory = await this.page.request.get(`${consoleUrl}/api/identity/organization/structure`, { headers: { 'X-Agentstration-Context': scope } });
    expect(inventory.status()).toBe(403);
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.openTenantCreate)).toHaveCount(0);
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.openCreate)).toHaveCount(0);
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.tenantRow)).toHaveCount(0);
    const identity = await this.page.request.get(`${consoleUrl}/api/identity/context`);
    expect(identity.ok()).toBeTruthy();
    const ownTenantId = (await identity.json()).context.tenantId;
    if (!ownTenantId) throw new Error('Viewer Tenant identifier missing.');
    const denied = await this.page.request.post(`${consoleUrl}/api/identity/organization/workspaces`, {
      headers: { 'X-Agentstration-Context': scope }, data: { tenantId: ownTenantId, name: 'forbidden-own-workspace', displayName: 'Forbidden own Workspace' },
    });
    expect(denied.status()).toBe(403);
    await this.inspectDeniedOperations(consoleUrl, scope, foreignTenantId);
    await this.assertWithinViewport();
  }

  public async currentScope(): Promise<string> {
    const shell = this.page.getByTestId(TestIds.console.shell);
    await expect(shell).toHaveAttribute('data-context-scope', /.+/);
    const scope = await shell.getAttribute('data-context-scope');
    if (!scope) throw new Error('Current context scope is missing.');
    return scope;
  }

  public async inspectRestrictedTenant(foreignName: string, locale: SupportedTestLocale): Promise<WorkspaceDefinition> {
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.openTenantCreate)).toHaveCount(0);
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.tenantRow)).toHaveCount(1);
    await expect(this.tenant(foreignName)).toHaveCount(0);
    const row = this.page.getByTestId(TestIds.organizationWorkspaces.tenantRow);
    const name = await row.getAttribute('data-tenant-name');
    if (!name) throw new Error('Tenant name missing.');
    const displayName = await row.locator('.tenant-toggle strong').innerText();
    await this.assertTenantContext(name, locale);
    return { name, displayName };
  }

  public async assertPersistedWorkspace(consoleUrl: string, tenantName: string, workspaceName: string): Promise<void> {
    const response = await this.page.request.get(`${consoleUrl}/api/identity/organization/structure`, { headers: { 'X-Agentstration-Context': '/instance' } });
    expect(response.ok()).toBeTruthy();
    const body = await response.json();
    const tenant = body.tenants.find((entry: { tenant: { name: string } }) => entry.tenant.name === tenantName);
    expect(tenant).toBeDefined();
    expect(tenant.workspaces.filter((workspace: { name: string }) => workspace.name === workspaceName)).toHaveLength(1);
  }

  public async currentTenantDefinition(): Promise<WorkspaceDefinition> {
    const row = this.page.getByTestId(TestIds.organizationWorkspaces.tenantRow);
    const name = await row.getAttribute('data-tenant-name');
    if (!name) throw new Error('Tenant name missing.');
    return { name, displayName: await row.locator('.tenant-toggle strong').innerText() };
  }

  public async submitInaccessibleWorkspace(definition: WorkspaceDefinition, originalScope: string): Promise<void> {
    await this.fillIdentity(definition);
    await this.page.getByTestId(TestIds.organizationWorkspaces.technicalName).press('Enter');
    await expect(this.createForm).toHaveCount(0);
    await expect(this.structure.locator('.structure-success')).toContainText(definition.displayName);
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-context-scope', originalScope);
    await expect(this.row(definition.name)).toHaveCount(0);
    await this.openContextSelector();
    await expect(this.page.locator(`[data-testid="${TestIds.console.contextOptionWorkspace}"][data-context-workspace-name="${definition.name}"]`)).toHaveCount(0);
    await this.page.getByTestId(TestIds.console.contextSelector).locator('.context-selector-close').click();
    await expect(this.page.getByTestId(TestIds.console.contextSelector)).toHaveCount(0);
    await this.assertWithinViewport();
  }

  public async fillIdentity(workspace: WorkspaceDefinition): Promise<void> {
    await fillAndCommit(this.page.getByTestId(TestIds.organizationWorkspaces.displayName), workspace.displayName);
    await fillAndCommit(this.page.getByTestId(TestIds.organizationWorkspaces.technicalName), workspace.name);
    await expect(this.page.getByTestId(TestIds.organizationWorkspaces.technicalName)).toHaveValue(workspace.name);
  }

  public async create(workspace: WorkspaceDefinition): Promise<string> {
    const existingId = await this.workspaceId(workspace.name);
    if (existingId) throw new Error(`Workspace '${workspace.name}' already exists.`);

    const row = this.row(workspace.name);
    await this.page.getByTestId(TestIds.organizationWorkspaces.create).click();
    await row.waitFor({ state: 'visible' });
    const workspaceId = await row.getAttribute('data-workspace-id');
    if (!workspaceId) throw new Error(`Workspace '${workspace.name}' was created without a visible identifier.`);
    return workspaceId;
  }

  public async select(workspaceId: string): Promise<void> {
    const shell = this.page.getByTestId(TestIds.console.shell);
    if (await shell.getAttribute('data-workspace-id') === workspaceId
      && await shell.getAttribute('data-context-kind') === 'Workspace') return;
    await this.openContextSelector();
    const exactOption = this.page.locator(`[data-testid="${TestIds.console.contextOptionWorkspace}"][data-context-workspace-id="${workspaceId}"]`);
    await exactOption.waitFor({ state: 'visible' });
    const scopeRef = await exactOption.getAttribute('data-context-scope-ref');
    if (!scopeRef) throw new Error(`Workspace '${workspaceId}' does not expose its Console context scope.`);
    await Promise.all([
      this.waitForContextNavigation(scopeRef),
      exactOption.click(),
    ]);
    await expect(shell).toHaveAttribute('data-context-kind', 'Workspace');
    await expect(shell).toHaveAttribute('data-context-scope', scopeRef);
    await expect(shell).toHaveAttribute('data-workspace-id', workspaceId);
  }

  public async selectByName(name: string): Promise<string> {
    const shell = this.page.getByTestId(TestIds.console.shell);
    await shell.waitFor({ state: 'visible' });
    if (await shell.getAttribute('data-workspace-name') === name) {
      const workspaceId = await shell.getAttribute('data-workspace-id');
      if (workspaceId) return workspaceId;
    }

    await this.openContextSelector();
    const selector = this.page.getByTestId(TestIds.console.contextSelector);
    const options = await selector.getByTestId(TestIds.console.contextOptionWorkspace).evaluateAll(elements => elements.map(element => ({
      name: element.getAttribute('data-context-workspace-name'),
      value: element.getAttribute('data-context-workspace-id'),
    })));
    const workspaceId = options.find(option => option.name === name)?.value;
    if (!workspaceId) {
      const availableNames = options.map(option => option.name).filter(Boolean).join(', ');
      throw new Error(`Campaign workspace '${name}' is not available in the workspace selector. Available workspaces: ${availableNames || 'none'}.`);
    }

    await this.select(workspaceId);
    await expect(shell).toHaveAttribute('data-workspace-name', name);
    return workspaceId;
  }

  public async selectInstance(): Promise<void> {
    const shell = this.page.getByTestId(TestIds.console.shell);
    if (await shell.getAttribute('data-context-kind') === 'Instance'
      && await shell.getAttribute('data-context-scope') === '/instance') return;
    await this.openContextSelector();
    await Promise.all([
      this.waitForContextNavigation('/instance'),
      this.page.getByTestId(TestIds.console.contextOptionInstance).click(),
    ]);
    await expect(shell).toHaveAttribute('data-context-kind', 'Instance');
    await expect(shell).toHaveAttribute('data-context-scope', '/instance');
  }

  public async selectTenantForWorkspace(name: string): Promise<string> {
    await this.openContextSelector();
    const workspace = this.page.locator(`[data-testid="${TestIds.console.contextOptionWorkspace}"][data-context-workspace-name="${name}"]`);
    await workspace.waitFor({ state: 'visible' });
    const workspaceId = await workspace.getAttribute('data-context-workspace-id');
    if (!workspaceId) throw new Error(`Workspace '${name}' does not expose its Console context identifier.`);
    return await this.selectTenantForWorkspaceId(workspaceId);
  }

  public async selectTenantForWorkspaceId(workspaceId: string): Promise<string> {
    await this.openContextSelector();
    const workspace = this.page.locator(`[data-testid="${TestIds.console.contextOptionWorkspace}"][data-context-workspace-id="${workspaceId}"]`);
    await workspace.waitFor({ state: 'visible' });
    const tenantId = await workspace.getAttribute('data-context-tenant-id');
    if (!tenantId) throw new Error(`Workspace '${workspaceId}' does not expose its Tenant context.`);
    const tenant = this.page.locator(`[data-testid="${TestIds.console.contextOptionTenant}"][data-context-tenant-id="${tenantId}"]`);
    const scopeRef = await tenant.getAttribute('data-context-scope-ref');
    if (!scopeRef) throw new Error(`Tenant context '${tenantId}' does not expose its scope.`);
    await Promise.all([
      this.waitForContextNavigation(scopeRef),
      tenant.click(),
    ]);
    const shell = this.page.getByTestId(TestIds.console.shell);
    await expect(shell).toHaveAttribute('data-context-kind', 'Tenant');
    await expect(shell).toHaveAttribute('data-context-scope', scopeRef);
    return scopeRef;
  }

  public row(name: string): Locator {
    return this.page.locator(
      `[data-testid="${TestIds.organizationWorkspaces.row}"][data-workspace-name="${name}"]`,
    );
  }

  public get structure(): Locator { return this.page.getByTestId(TestIds.organizationWorkspaces.structure); }

  public get createForm(): Locator { return this.page.getByTestId(TestIds.organizationWorkspaces.createForm); }

  private async workspaceId(name: string): Promise<string | null> {
    const row = this.row(name);
    return await row.count() === 0 ? null : await row.first().getAttribute('data-workspace-id');
  }

  private async openContextSelector(): Promise<void> {
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-preferences-ready', 'true');
    const selector = this.page.getByTestId(TestIds.console.contextSelector);
    const trigger = this.page.getByTestId(TestIds.console.contextSelectorTrigger);
    await expect(async () => {
      if (await selector.count() === 0) {
        await expect(trigger).toBeEnabled();
        await trigger.click();
      }
      await expect(selector).toBeVisible();
    }).toPass({ timeout: 30_000 });
  }

  private async waitForContextNavigation(scopeRef: string): Promise<void> {
    await this.page.waitForURL(url => url.searchParams.get('context') === scopeRef, { waitUntil: 'domcontentloaded' });
    await this.page.getByTestId(TestIds.console.shell).waitFor({ state: 'visible' });
  }
}
