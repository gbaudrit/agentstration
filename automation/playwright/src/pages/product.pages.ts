import type { Locator, Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { AgentEditorPage } from './agent-editor.page.js';
import { FlowEditorPage } from './flow-editor.page.js';
import { EntryEditorPage } from './entry-editor.page.js';
import { LoginPage } from './login.page.js';
import { OrganizationWorkspacesPage } from './organization-workspaces.page.js';
import { WorkplacePage } from './workplace.page.js';
import { DashboardEditorPage } from './dashboard-editor.page.js';
import { DistributionPage } from './distribution.page.js';
import { FlowObservabilityPage } from './flow-observability.page.js';
import { ConsoleAdministrationPage } from './console-administration.page.js';

export class ProductPages {
  public readonly agentEditor: AgentEditorPage;
  public readonly flowEditor: FlowEditorPage;
  public readonly entryEditor: EntryEditorPage;
  public readonly login: LoginPage;
  public readonly organizationWorkspaces: OrganizationWorkspacesPage;
  public readonly workplace: WorkplacePage;
  public readonly dashboardEditor: DashboardEditorPage;
  public readonly distribution: DistributionPage;
  public readonly flowObservability: FlowObservabilityPage;
  public readonly consoleAdministration: ConsoleAdministrationPage;

  public constructor(public readonly page: Page) {
    this.agentEditor = new AgentEditorPage(page);
    this.flowEditor = new FlowEditorPage(page);
    this.entryEditor = new EntryEditorPage(page);
    this.login = new LoginPage(page);
    this.organizationWorkspaces = new OrganizationWorkspacesPage(page);
    this.workplace = new WorkplacePage(page);
    this.dashboardEditor = new DashboardEditorPage(page);
    this.distribution = new DistributionPage(page);
    this.flowObservability = new FlowObservabilityPage(page);
    this.consoleAdministration = new ConsoleAdministrationPage(page);
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
