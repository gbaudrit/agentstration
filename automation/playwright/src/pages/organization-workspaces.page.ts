import type { Locator, Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

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
    await this.page.getByTestId(TestIds.organizationWorkspaces.displayName).fill(workspace.displayName);
    await this.page.getByTestId(TestIds.organizationWorkspaces.technicalName).fill(workspace.name);
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
    const selector = this.page.getByTestId(TestIds.console.workspaceSelector);
    await selector.waitFor({ state: 'visible' });
    await selector.selectOption(workspaceId);
    await this.page.locator(`[data-testid="${TestIds.console.shell}"][data-workspace-id="${workspaceId}"]`).waitFor({ state: 'visible' });
  }

  public async selectByName(name: string): Promise<void> {
    const shell = this.page.getByTestId(TestIds.console.shell);
    await shell.waitFor({ state: 'visible' });
    if (await shell.getAttribute('data-workspace-name') === name) return;

    const selector = this.page.getByTestId(TestIds.console.workspaceSelector);
    if (await selector.count() === 0) {
      throw new Error(`Campaign workspace '${name}' is not available in the workspace selector.`);
    }

    const options = await selector.locator('option').evaluateAll(elements => elements.map(element => ({
      name: element.getAttribute('data-workspace-name'),
      value: (element as HTMLOptionElement).value,
    })));
    const workspaceId = options.find(option => option.name === name)?.value;
    if (!workspaceId) {
      const availableNames = options.map(option => option.name).filter(Boolean).join(', ');
      throw new Error(`Campaign workspace '${name}' is not available in the workspace selector. Available workspaces: ${availableNames || 'none'}.`);
    }

    await this.select(workspaceId);
    if (await shell.getAttribute('data-workspace-name') !== name) {
      throw new Error(`Workspace selector did not activate campaign workspace '${name}'.`);
    }
  }

  public row(name: string): Locator {
    return this.page.getByTestId(TestIds.organizationWorkspaces.row).filter({
      has: this.page.locator('code').getByText(name, { exact: true }),
    });
  }

  public get createForm(): Locator { return this.page.getByTestId(TestIds.organizationWorkspaces.createForm); }

  private async workspaceId(name: string): Promise<string | null> {
    const row = this.row(name);
    return await row.count() === 0 ? null : await row.first().getAttribute('data-workspace-id');
  }
}
