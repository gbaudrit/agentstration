import { type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

type OperationsMarker = keyof Pick<typeof TestIds.operations, 'accessDenied' | 'management' | 'cleanup'>;

export class OperationsPage {
  public constructor(private readonly page: Page) {}

  public async open(consoleUrl: string, path: string, marker: OperationsMarker): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}${path}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Operations route '${path}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.operations[marker]).waitFor({ state: 'visible' });
  }

  public async validateCleanupConfirmation(): Promise<boolean> {
    const candidate = this.page.locator('[data-testid="operations-cleanup"] .cleanup-row input[type="checkbox"]').first();
    if (!await candidate.isVisible()) return false;
    await candidate.check();
    await this.page.getByTestId(TestIds.operations.cleanupOpenConfirmation).click();
    const confirm = this.page.getByTestId(TestIds.operations.cleanupConfirm);
    await confirm.waitFor({ state: 'visible' });
    if (!await confirm.isDisabled()) throw new Error('Cleanup confirmation must require explicit acknowledgement.');
    await this.page.getByTestId(TestIds.operations.cleanupAcknowledgement).check();
    if (!await confirm.isEnabled()) throw new Error('Cleanup confirmation did not become available after acknowledgement.');
    await this.page.getByTestId(TestIds.operations.cleanupCancel).click();
    await confirm.waitFor({ state: 'detached' });
    return true;
  }
}
