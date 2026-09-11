import type { Locator, Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

export class DashboardEditorPage {
  public constructor(private readonly page: Page) {}

  public async openWithEntry(consoleUrl: string, entryName: string, entryNamespace = 'default'): Promise<void> {
    const query = new URLSearchParams({ entry: entryName, entryNamespace });
    const response = await this.page.goto(`${consoleUrl}/workspaces?${query}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Dashboard editor returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.editor.waitFor({ state: 'visible' });
    await this.entryRow(entryName, entryNamespace).waitFor({ state: 'visible' });
  }

  public async includeEntry(entryName: string, entryNamespace = 'default'): Promise<void> {
    await this.entryRow(entryName, entryNamespace).getByTestId(TestIds.dashboardEditor.entryToggle).setChecked(true);
  }

  public async publish(): Promise<void> {
    await this.page.getByTestId(TestIds.dashboardEditor.publish).click();
    await this.page.locator(
      `[data-testid="${TestIds.dashboardEditor.status}"][data-state="success"]`,
    ).waitFor({ state: 'visible' });
  }

  public entryRow(entryName: string, entryNamespace = 'default'): Locator {
    return this.page.locator(
      `[data-testid="${TestIds.dashboardEditor.entryRow}"][data-entry-id="${attributeValue(entryName)}"][data-entry-namespace="${attributeValue(entryNamespace)}"]`,
    );
  }

  public get editor(): Locator { return this.page.getByTestId(TestIds.dashboardEditor.editor); }
}

function attributeValue(value: string): string {
  return value.replaceAll('\\', '\\\\').replaceAll('"', '\\"');
}
