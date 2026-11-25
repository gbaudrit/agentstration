import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

interface FunctionRowGeometry {
  widths: number[];
  height: number;
  descriptionWidth: number;
}

export class TransformationFunctionCollectionPage {
  public constructor(private readonly page: Page) {}

  public get catalogue(): Locator {
    return this.page.getByTestId(TestIds.transformationFunctions.collection);
  }

  public async inspectTable(): Promise<FunctionRowGeometry[]> {
    await this.catalogue.locator('.collection-view-switch button').nth(0).click();
    const table = this.catalogue.locator('table');
    await expect(table).toBeVisible();
    await expect(table.locator('thead th')).toHaveCount(5);
    const rows = table.getByTestId(TestIds.transformationFunctions.row);
    await expect(rows.first()).toBeVisible();
    const geometry = await rows.evaluateAll(elements => elements.map(row => {
      const cells = [...row.querySelectorAll('td')];
      return {
        widths: cells.map(cell => cell.getBoundingClientRect().width),
        height: row.getBoundingClientRect().height,
        descriptionWidth: cells[3].querySelector('p')?.getBoundingClientRect().width ?? 0,
      };
    }));
    for (const row of geometry) {
      expect(row.widths).toHaveLength(5);
      expect(row.widths[3], 'Saved descriptions retain readable space beside Actions').toBeGreaterThanOrEqual(240);
      expect(row.descriptionWidth).toBeGreaterThanOrEqual(200);
      expect(row.widths[4], 'Actions retain room for their menu control').toBeGreaterThanOrEqual(104);
      expect(row.widths[4]).toBeLessThanOrEqual(160);
      expect(row.height, 'Descriptions must not wrap one character per line').toBeLessThan(360);
    }
    const headings = await table.locator('thead th').evaluateAll(elements => elements.map(cell => {
      const range = document.createRange();
      range.selectNodeContents(cell);
      const text = range.getBoundingClientRect();
      const bounds = cell.getBoundingClientRect();
      return { textRight: text.right, right: bounds.right };
    }));
    for (const heading of headings) expect(heading.textRight).toBeLessThanOrEqual(heading.right + 1);

    await this.inspectMenu(rows.first());
    await table.evaluate(element => { element.closest('.data-grid-wrap')!.scrollLeft = 0; });
    await this.assertDocumentBounds();
    return geometry;
  }

  public async inspectCards(): Promise<void> {
    await this.catalogue.locator('.collection-view-switch button').nth(1).click();
    const cards = this.catalogue.locator('.collection-card');
    await expect(cards.first()).toBeVisible();
    for (const card of await cards.all()) {
      const description = card.getByTestId(TestIds.transformationFunctions.description);
      await expect(description).toBeVisible();
      expect((await description.boundingBox())!.width).toBeGreaterThanOrEqual(180);
      await expect(card.locator('.collection-actions-menu > summary')).toBeVisible();
    }
    await this.inspectMenu(cards.first());
    await this.assertDocumentBounds();
  }

  public async capture(): Promise<Buffer> {
    await this.page.evaluate(() => window.scrollTo(0, 0));
    return this.page.screenshot({ fullPage: true });
  }

  private async inspectMenu(row: Locator): Promise<void> {
    const menu = row.locator('.collection-actions-menu');
    await expect(async () => {
      if (!await menu.locator('.collection-actions-panel').isVisible()) {
        if (await this.page.evaluate(() => navigator.maxTouchPoints > 0)) await menu.locator('summary').tap();
        else await menu.locator('summary').click();
      }
      await expect(menu.locator('.collection-actions-panel')).toBeVisible();
    }).toPass({ timeout: 5000 });
    await this.page.keyboard.press('Escape');
    await expect(menu.locator('.collection-actions-panel')).toBeHidden();
  }

  private async assertDocumentBounds(): Promise<void> {
    const overflow = await this.page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
    expect(overflow).toBeLessThanOrEqual(1);
  }
}
