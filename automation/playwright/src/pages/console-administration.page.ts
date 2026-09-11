import { type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit } from './controls.js';

type AdministrationMarker = keyof Pick<typeof TestIds.consoleAdministration,
  'settings' | 'profileSettings' | 'organization' | 'organizationAccess' |
  'organizationMembers' | 'organizationMemberDetails' | 'organizationSecurityAudit' |
  'accountPat' | 'accountSecurity' | 'logout'>;

export class ConsoleAdministrationPage {
  public constructor(private readonly page: Page) {}

  public async open(consoleUrl: string, path: string, marker: AdministrationMarker): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}${path}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Console administration route '${path}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.consoleAdministration[marker]).waitFor({ state: 'visible' });
  }

  public async openBootstrapRedirect(consoleUrl: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/bootstrap`, { waitUntil: 'domcontentloaded' });
    if (response?.status() === 404) return;
    if (!response?.ok()) throw new Error(`Bootstrap route returned HTTP ${response?.status() ?? 'no response'}.`);
    if (new URL(this.page.url()).pathname === '/bootstrap') {
      await this.page.getByTestId(TestIds.consoleAdministration.bootstrap).waitFor({ state: 'visible' });
      return;
    }
    await this.page.getByTestId(TestIds.console.shell).waitFor({ state: 'visible' });
  }

  public async openWorkspaces(consoleUrl: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/workspaces`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Workspaces route returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.dashboardEditor.editor).waitFor({ state: 'visible' });
  }

  public async openFirstMember(): Promise<string> {
    const link = this.page.locator('[data-testid="console-admin-organization-members"] a[href^="/settings/organization/members/"]').first();
    await link.waitFor({ state: 'visible' });
    const href = await link.getAttribute('href');
    if (!href) throw new Error('The organization member list does not expose a details link.');
    const memberId = href.split('/').at(-1)!;
    if (!/^[0-9a-f-]{36}$/i.test(memberId)) throw new Error(`Invalid member identifier '${memberId}'.`);
    await link.click();
    await this.page.getByTestId(TestIds.consoleAdministration.organizationMemberDetails).waitFor({ state: 'visible' });
    await this.page.getByTestId(TestIds.consoleAdministration.organizationMemberDetails).locator('article.panel').first().waitFor({ state: 'visible' });
    return memberId;
  }

  public async selectTheme(theme: 'system' | 'light' | 'dark'): Promise<void> {
    const option = this.page.locator(`[data-testid="${TestIds.consoleAdministration.profileThemeOption}"][data-theme="${theme}"]`);
    await option.click();
    const effectiveTheme = theme === 'system' ? 'dark' : theme;
    await this.page.locator(`[data-testid="${TestIds.console.shell}"].theme-${effectiveTheme}`).waitFor({ state: 'visible' });
  }

  public async selectLanguage(language: 'auto' | 'en-US' | 'fr-FR'): Promise<void> {
    await Promise.all([
      this.page.waitForEvent('framenavigated'),
      this.page.getByTestId(TestIds.consoleAdministration.profileLanguage).selectOption(language),
    ]);
    await this.page.getByTestId(TestIds.consoleAdministration.profileSettings).waitFor({ state: 'visible' });
    if (language !== 'auto') await this.page.locator(`html[lang="${language}"]`).waitFor({ state: 'attached' });
  }

  public async createAndRevokeToken(name: string): Promise<void> {
    await fillAndCommit(this.page.getByTestId(TestIds.consoleAdministration.patName), name);
    const permission = this.page.getByTestId(TestIds.consoleAdministration.patPermission).first();
    if (!await permission.isChecked()) await permission.check();
    await this.page.getByTestId(TestIds.consoleAdministration.patCreate).click();
    const row = this.page.getByTestId(TestIds.consoleAdministration.patTokenRow).filter({ hasText: name });
    await row.waitFor({ state: 'visible' });
    await row.getByTestId(TestIds.consoleAdministration.patRevoke).click();
    await row.getByTestId(TestIds.consoleAdministration.patRevoke).waitFor({ state: 'detached' });
  }

  public async exerciseShellNavigation(): Promise<void> {
    await this.page.getByTestId(TestIds.console.breadcrumb).waitFor({ state: 'visible' });
    const sidebarToggle = this.page.getByTestId(TestIds.console.sidebarToggle);
    await sidebarToggle.click();
    await this.page.locator(`[data-testid="${TestIds.console.shell}"].sidebar-collapsed`).waitFor();
    await sidebarToggle.click();
    await this.page.getByTestId(TestIds.console.commandTrigger).click();
    await this.page.getByTestId(TestIds.console.commandPalette).waitFor({ state: 'visible' });
    await fillAndCommit(this.page.getByTestId(TestIds.console.commandInput), 'settings');
    await this.page.getByTestId(TestIds.console.commandPalette).getByRole('option').first().waitFor({ state: 'visible' });
    await this.page.keyboard.press('Escape');
    await this.page.getByTestId(TestIds.console.commandPalette).waitFor({ state: 'detached' });
    await this.page.getByTestId(TestIds.console.notificationsTrigger).click();
    await this.page.getByTestId(TestIds.console.notificationsPanel).waitFor({ state: 'visible' });
  }

  public async signOut(): Promise<void> {
    await Promise.all([
      this.page.waitForURL(url => url.pathname !== '/logout'),
      this.page.getByTestId(TestIds.consoleAdministration.logoutSubmit).click(),
    ]);
    const path = new URL(this.page.url()).pathname;
    if (path !== '/' && path !== '/login') throw new Error(`Logout redirected to unexpected path '${path}'.`);
  }
}
