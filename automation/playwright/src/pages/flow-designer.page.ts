import { expect, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

export class FlowDesignerPage {
  public constructor(private readonly page: Page) {}

  public async open(consoleUrl: string, namespace: string, name: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/namespaces/${encodeURIComponent(namespace)}/flows/${encodeURIComponent(name)}/designer`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Flow designer returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(TestIds.flowObservability.designer).waitFor({ state: 'visible' });
  }

  public async validate(): Promise<void> {
    await this.page.getByTestId(TestIds.flowObservability.designerValidate).click();
  }

  public async save(): Promise<void> {
    await this.page.getByTestId(TestIds.flowObservability.designerSave).click();
  }

  public get designer() { return this.page.getByTestId(TestIds.flowObservability.designer); }
}
