import { expect, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

export class FlowDesignerPage {
  public constructor(private readonly page: Page) {}

  public async createDraftAndOpen(consoleUrl: string): Promise<void> {
    const name = `designer-links-smoke-${Date.now()}`;
    const response = await this.page.request.post(`${consoleUrl}/api/flows/drafts`, {
      data: { name, displayName: 'Designer links smoke', template: 'Empty' },
    });
    expect(response.status(), await response.text()).toBe(201);
    await this.open(consoleUrl, 'default', name);
  }

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

  public async expectExistingLinksVisible(): Promise<void> {
    const links = this.page.locator('svg .diagram-link');
    await expect(links.first()).toBeVisible();
  }

  public async expectCanvasFitsViewportAndWheelZoomContract(): Promise<void> {
    const canvas = this.page.locator('.flow-canvas-wrap');
    await expect(canvas).toBeVisible();
    const bounds = await canvas.boundingBox();
    const viewport = this.page.viewportSize();
    expect(bounds).not.toBeNull();
    expect(viewport).not.toBeNull();
    expect(bounds!.y + bounds!.height).toBeLessThanOrEqual(viewport!.height + 1);

    const zoom = this.page.locator('.flow-zoom-controls span');
    const initialZoom = await zoom.textContent();
    await canvas.hover();
    await this.page.mouse.wheel(0, 480);
    await expect(zoom).toHaveText(initialZoom ?? '');

    await canvas.hover();
    await this.page.keyboard.down('Control');
    await this.page.mouse.wheel(0, -480);
    await this.page.keyboard.up('Control');
    await expect(zoom).not.toHaveText(initialZoom ?? '');
  }

  public async selectFirstInspectorTransition(): Promise<void> {
    const linkPath = this.page.locator('svg .diagram-link path').first();
    const initialStroke = await linkPath.getAttribute('stroke');
    await this.page.locator('.transition-list li > button:first-child').click();
    await expect(this.page.locator('.transition-editor')).toBeVisible();
    await expect.poll(async () => await linkPath.getAttribute('stroke')).not.toBe(initialStroke);
  }

  public get designer() { return this.page.getByTestId(TestIds.flowObservability.designer); }
}
