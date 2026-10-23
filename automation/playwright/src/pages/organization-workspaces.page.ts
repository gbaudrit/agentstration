import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit } from './controls.js';

export interface WorkspaceDefinition {
  name: string;
  displayName: string;
}

export class OrganizationWorkspacesPage {
  public constructor(private readonly page: Page) {}

  public async open(consoleUrl: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/settings/organization/workspaces`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Organization workspaces page returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.locator(`[data-testid="${TestIds.organizationWorkspaces.createForm}"][data-interactive="true"]`).waitFor({ state: 'visible' });
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
    await exactOption.click();
    await expect(shell).toHaveAttribute('data-context-kind', 'Workspace');
    await expect(shell).toHaveAttribute('data-context-scope', scopeRef);
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
    if (await shell.getAttribute('data-context-kind') === 'Instance') return;
    await this.openContextSelector();
    await this.page.getByTestId(TestIds.console.contextOptionInstance).click();
    await expect(shell).toHaveAttribute('data-context-kind', 'Instance');
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
    await tenant.click();
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-context-kind', 'Tenant');
    return scopeRef;
  }

  public row(name: string): Locator {
    return this.page.locator(
      `[data-testid="${TestIds.organizationWorkspaces.row}"][data-workspace-name="${name}"]`,
    );
  }

  public get createForm(): Locator { return this.page.getByTestId(TestIds.organizationWorkspaces.createForm); }

  private async workspaceId(name: string): Promise<string | null> {
    const row = this.row(name);
    return await row.count() === 0 ? null : await row.first().getAttribute('data-workspace-id');
  }

  private async openContextSelector(): Promise<void> {
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-preferences-ready', 'true');
    const selector = this.page.getByTestId(TestIds.console.contextSelector);
    if (await selector.count() === 0) {
      const trigger = this.page.getByTestId(TestIds.console.contextSelectorTrigger);
      await expect(trigger).toBeEnabled();
      await trigger.click();
    }
    await selector.waitFor({ state: 'visible' });
  }
}
