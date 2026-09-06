import type { Page } from '@playwright/test';
import { AgentEditorPage } from './agent-editor.page.js';
import { LoginPage } from './login.page.js';

export class ProductPages {
  public readonly agentEditor: AgentEditorPage;
  public readonly login: LoginPage;

  public constructor(public readonly page: Page) {
    this.agentEditor = new AgentEditorPage(page);
    this.login = new LoginPage(page);
  }

  public async ensureTheme(theme: 'light' | 'dark'): Promise<void> {
    const shell = this.page.getByTestId('console-shell');
    await this.page.locator('[data-testid="console-shell"][data-preferences-ready="true"]').waitFor({ state: 'visible' });
    if (await shell.evaluate((element, expected) => element.classList.contains(`theme-${expected}`), theme)) return;
    await this.page.getByTestId('theme-toggle').click();
    await this.page.locator(`[data-testid="console-shell"].theme-${theme}`).waitFor({ state: 'visible' });
  }
}
