import { expect, type Locator, type Page, type TestInfo } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

export type LayoutFamily = 'artifacts' | 'queries' | 'functions';

/** Focused maintenance of #1257's three reported layout regressions. */
export class CollectionLayoutPage {
  public constructor(private readonly page: Page, private readonly family: LayoutFamily, private readonly artifactScope: string) {}
  private get root(): Locator {
    return this.page.getByTestId(this.family === 'artifacts' ? TestIds.artifacts.list
      : this.family === 'queries' ? TestIds.collectionLayout.queries : TestIds.transformationFunctions.collection);
  }
  private get rows(): Locator {
    return this.root.getByTestId(this.family === 'artifacts' ? TestIds.collectionLayout.artifactRow
      : this.family === 'queries' ? TestIds.collectionLayout.queryRow : TestIds.transformationFunctions.row);
  }

  public async inspect(consoleUrl: string, info: TestInfo): Promise<void> {
    const route = this.family === 'queries' ? '/data-sources/layout-query-dataset?composition=dataset&namespace=default&scopeRef=%2Finstance&context=%2Finstance#queries'
      : this.family === 'artifacts' ? `/artifacts?context=${encodeURIComponent(this.artifactScope)}` : '/transformation-functions?context=%2Finstance';
    await this.page.goto(`${consoleUrl}${route}`);
    if (this.family === 'functions') {
      // Native functions can grow independently of this layout fixture.
      // Require each long fixture identity; geometry below still checks every row.
      for (const index of [1, 2, 3]) {
        await expect(this.rows.filter({ has: this.page.getByRole('link', { name: new RegExp(`^AAA Function ${index} —`) }) })).toHaveCount(1);
      }
    } else {
      await expect(this.rows).toHaveCount(3);
    }
    for (const view of ['table', 'cards'] as const) {
      const viewButton = this.root.locator('.collection-view-switch button').nth(view === 'table' ? 0 : 1);
      await viewButton.evaluate(el => el.scrollIntoView({ block: 'center' }));
      await viewButton.click({ timeout: 10_000 });
      await expect(this.rows.first()).toBeVisible();
      const wrap = this.root.locator('.data-grid-wrap');
      if (view === 'table') await wrap.evaluate(el => { el.scrollLeft = 0; });
      const before = await this.geometry();
      await this.capture(info, `${view}-closed`);
      const menu = this.rows.first().locator('.collection-actions-menu');
      const trigger = menu.locator('summary');
      await trigger.evaluate(el => el.scrollIntoView({ block: 'center' }));
      if (this.page.viewportSize()!.width < 761) await trigger.tap({ timeout: 10_000 }); else await trigger.click({ timeout: 10_000 });
      await expect(menu.locator('.collection-actions-panel')).toBeVisible();
      await this.page.keyboard.press('Escape');
      await expect(menu.locator('.collection-actions-panel')).toBeHidden();
      await expect(trigger).toBeFocused();
      await this.page.keyboard.press('End');
      const actions = await this.geometry();
      await this.capture(info, `${view}-actions`);
      const preview = this.family === 'artifacts' ? TestIds.collectionLayout.artifactPreview
        : this.family === 'queries' ? TestIds.collectionLayout.queryPreview : TestIds.collectionLayout.functionPreview;
      await expect(this.rows.first().getByTestId(preview)).toBeFocused();
      const panel = (await menu.locator('.collection-actions-panel').boundingBox())!;
      expect(panel.x).toBeGreaterThanOrEqual(0);
      expect(panel.x + panel.width).toBeLessThanOrEqual(this.page.viewportSize()!.width);
      expect(panel.y + panel.height).toBeLessThanOrEqual(this.page.viewportSize()!.height);
      await this.rows.first().getByTestId(preview).click();
      const dialog = this.page.getByRole('dialog');
      await expect(dialog).toBeVisible();
      await expect(this.page.getByTestId(TestIds.collectionLayout.previewContent).locator('.resource-summary').first()).toBeVisible();
      const opened = await this.geometry();
      await this.capture(info, `${view}-preview`);
      await dialog.locator('.resource-preview-heading button').click();
      await expect(dialog).toHaveCount(0);
      await expect(trigger).toBeFocused();
      await expect(this.rows.first()).toBeVisible();
      if (view === 'table') await expect(this.root.locator('table')).toBeVisible();
      const closed = await this.geometry();
      await this.capture(info, `${view}-after-close`);
      await info.attach(`${this.family}-${view}-geometry`, { body: JSON.stringify({ before, actions, opened, closed }, null, 2), contentType: 'application/json' });
      for (const phase of [before, actions, opened, closed]) {
        expect(phase.documentOverflow).toBeLessThanOrEqual(1);
        for (const row of phase.rows) {
          expect(row.height).toBeLessThan(view === 'table' ? 400 : 1000);
          expect(row.actionHeight, 'Actions must never wrap one character per line').toBeLessThanOrEqual(48);
          if (this.family === 'functions' && view === 'table') expect(row.descriptionWidth).toBeGreaterThanOrEqual(200);
          expect(row.iconTextOverlap).toBe(false);
          expect(row.badgeIconOverlap).toBe(false);
        }
      }
      if (view === 'table') {
        expect(before.headers.at(-1)!.width, 'Reserve the full Actions menu budget beside long names').toBeGreaterThanOrEqual(104);
        if (this.family !== 'functions' && this.page.viewportSize()!.width === 1440) {
          expect(before.scrollWidth, 'These native desktop columns fit without moving the identity off screen').toBeLessThanOrEqual(before.clientWidth + 1);
          for (const phase of [before, actions, opened, closed]) expect(phase.identityVisible).toBe(true);
        }
        // Menus and a modal must not move the underlying reading position.
        expect(opened.scrollLeft).toBe(actions.scrollLeft);
        expect(closed.scrollLeft).toBe(opened.scrollLeft);
        if (this.family === 'artifacts') for (const phase of [before, actions, opened, closed]) expect(phase.identityVisible).toBe(true);
        for (const header of before.headers) {
          expect(header.textRight).toBeLessThanOrEqual(header.right + 1);
          expect(header.width).toBeGreaterThan(40);
        }
        // All metadata remains reachable; the document itself never scrolls sideways.
        await wrap.evaluate(el => { el.scrollLeft = el.scrollWidth; });
        const scrolled = await this.geometry();
        if (this.family === 'artifacts') expect(scrolled.identityVisible).toBe(true);
        await wrap.evaluate(el => { el.scrollLeft = 0; });
        expect((await this.geometry()).identityVisible).toBe(true);
      }
    }
  }

  private async geometry() {
    return this.root.evaluate((root, descriptionId) => {
      const wrap = root.querySelector('.data-grid-wrap');
      const intersect = (a: DOMRect, b: DOMRect) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
      const rows = [...root.querySelectorAll('tbody tr, .collection-card')].map(row => {
        const icon = row.querySelector('.resource-icon')!.getBoundingClientRect();
        const text = row.querySelector('.collection-identity-text')!.getBoundingClientRect();
        const badge = row.querySelector('.resource-scope-badge')?.getBoundingClientRect();
        return { height: row.getBoundingClientRect().height, iconLeft: icon.left, textLeft: text.left,
          actionHeight: row.querySelector('.collection-actions-menu > summary')!.getBoundingClientRect().height,
          descriptionWidth: row.querySelector(`[data-testid="${descriptionId}"]`)?.getBoundingClientRect().width ?? null,
          iconTextOverlap: intersect(icon, text), badgeIconOverlap: badge ? intersect(icon, badge) : false };
      });
      const header = root.querySelector('thead th');
      const headers = [...root.querySelectorAll('thead th')].map(cell => {
        const range = document.createRange(); range.selectNodeContents(cell);
        const rect = cell.getBoundingClientRect();
        return { width: rect.width, right: rect.right, textRight: range.getBoundingClientRect().right };
      });
      return { scrollLeft: wrap?.scrollLeft ?? 0, scrollWidth: wrap?.scrollWidth ?? 0, clientWidth: wrap?.clientWidth ?? 0,
        documentOverflow: document.documentElement.scrollWidth - innerWidth,
        identityVisible: !wrap || (rows.every(row => row.iconLeft >= wrap.getBoundingClientRect().left)
          && (!header || header.getBoundingClientRect().left >= wrap.getBoundingClientRect().left - 1)), rows, headers };
    }, TestIds.transformationFunctions.description);
  }

  private async capture(info: TestInfo, phase: string): Promise<void> {
    await info.attach(`${this.family}-${phase}`, { body: await this.page.screenshot({ fullPage: false }), contentType: 'image/png' });
  }
}
