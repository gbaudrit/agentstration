import type { Locator, Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { AgentEditorPage } from './agent-editor.page.js';
import { LoginPage } from './login.page.js';
import { OrganizationWorkspacesPage } from './organization-workspaces.page.js';

export class ProductPages {
  public readonly agentEditor: AgentEditorPage;
  public readonly login: LoginPage;
  public readonly organizationWorkspaces: OrganizationWorkspacesPage;

  public constructor(public readonly page: Page) {
    this.agentEditor = new AgentEditorPage(page);
    this.login = new LoginPage(page);
    this.organizationWorkspaces = new OrganizationWorkspacesPage(page);
  }

  public async ensureTheme(theme: 'light' | 'dark'): Promise<void> {
    const shell = this.page.getByTestId(TestIds.console.shell);
    await this.page.locator(`[data-testid="${TestIds.console.shell}"][data-preferences-ready="true"]`).waitFor({ state: 'visible' });
    if (await shell.evaluate((element, expected) => element.classList.contains(`theme-${expected}`), theme)) return;
    await this.page.getByTestId(TestIds.console.themeToggle).click();
    await this.page.locator(`[data-testid="${TestIds.console.shell}"].theme-${theme}`).waitFor({ state: 'visible' });
  }

  public get readyPlatformOverview(): Locator {
    return this.page.locator(`[data-testid="${TestIds.console.platformOverview}"][aria-busy="false"]`);
  }
}
