import { ProductPages } from '../src/pages/product.pages.js';
import { TestIds } from '../src/contracts/test-ids.js';
import { createAgent, type CreateAgentInput } from '../src/journeys/create-agent.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { expect, test } from '../src/fixtures/test.js';

const agent: CreateAgentInput = {
  name: 'playwright-welcome-navigation',
  displayName: 'Playwright welcome agent',
  description: 'Welcomes users during the browser journey.',
  instructions: 'Welcome the user and answer concisely.',
  modelProfile: ['/instance|default|missing-profile', '/instance|default|reasoning-default'],
  runtimeProfile: '/instance|default|maf-builtin',
};

for (const variant of [
  { name: 'desktop English dark', locale: 'en-US', theme: 'dark', width: 1440, height: 1000, touch: false, tag: '' },
  { name: 'mobile French light', locale: 'fr-FR', theme: 'light', width: 390, height: 844, touch: true, tag: '@responsive' },
] as const) {
  test.describe(variant.name, () => {
    test.use({ locale: variant.locale, colorScheme: variant.theme, hasTouch: variant.touch, viewport: { width: variant.width, height: variant.height } });
    test.afterEach(async ({ page, product }) => {
      if (variant.locale !== 'en-US') await new ProductPages(page).consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
    });
    test(`an administrator can create and deploy an agent ${variant.tag} @smoke`, async ({ page, product }, testInfo) => {
      const pages = new ProductPages(page);
      await pages.login.signIn(product.consoleUrl);
      await pages.consoleAdministration.restoreLanguage(product.consoleUrl, variant.locale);
      const fixture = { ...agent, name: agent.name + (variant.touch ? '-mobile' : '') };
      await createAgent({
        ...product,
        pages: new ProductPages(page),
        checkpoint: ignoreCheckpoints,
        theme: variant.theme,
      }, fixture);

      await expect(page).toHaveURL(url =>
        url.pathname === `/agents/${fixture.name}` && url.searchParams.has('scopeRef'));
      await expect(page).toHaveURL(url => url.hash === '#definition/identity');
      await expect(page.getByTestId(TestIds.agentEditor.name)).toHaveValue(fixture.name);
      await expect(page.getByTestId(TestIds.agentEditor.name)).toBeDisabled();
      await pages.agentEditor.openOverview();
      await page.evaluate(() => window.scrollTo(0, 0));
      await pages.agentEditor.expectStandardOverview(variant.locale);
      await pages.agentEditor.expectSavedDisplayName(fixture.displayName);
      await expect(page.getByTestId('agent-configuration-summary')).toContainText(fixture.instructions);
      await expect(page.getByTestId('agent-configuration-summary').locator('input, textarea')).toHaveCount(0);
      const header = await page.locator('.resource-details-template > .resource-header').boundingBox();
      const main = await page.locator('.resource-details-main').boundingBox();
      const rail = await page.locator('.resource-details-navigation').boundingBox();
      expect(header).not.toBeNull(); expect(main).not.toBeNull(); expect(rail).not.toBeNull();
      if (!variant.touch) {
        expect(rail!.x - (main!.x + main!.width)).toBeGreaterThanOrEqual(16);
        expect(header!.y + header!.height).toBeLessThanOrEqual(main!.y);
        const value = await page.getByTestId('agent-configuration-summary').locator('th:last-child').boundingBox();
        const table = await page.getByTestId('agent-configuration-summary').locator('table').boundingBox();
        expect(value!.width / table!.width).toBeGreaterThan(0.6);
      }
      await testInfo.attach('agent-template-overview', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });
      await pages.agentEditor.openEdit();
      await page.getByTestId(TestIds.agentEditor.displayName).fill('Template saved from overview');
      await page.getByTestId(TestIds.agentEditor.displayName).blur();
      await pages.agentEditor.openOverview();
      await pages.agentEditor.expectSavedDisplayName(fixture.displayName);
      const save = page.getByTestId(TestIds.agentEditor.saveDraft);
      await save.scrollIntoViewIfNeeded();
      const saveBox = await save.boundingBox();
      if (variant.touch) {
        const mobileNavigation = await page.getByTestId(TestIds.console.sidebar).boundingBox();
        expect(saveBox!.y + saveBox!.height).toBeLessThanOrEqual(mobileNavigation!.y);
      } else expect(saveBox!.x + saveBox!.width).toBeLessThan(rail!.x);
      await save.click();
      await pages.agentEditor.expectSavedDisplayName('Template saved from overview');
      await expect(page.locator('.unsaved-changes-bar')).toHaveCount(0);
      await pages.agentEditor.openEdit();
      await page.getByTestId(TestIds.agentEditor.displayName).fill('Discard this draft');
      await page.getByTestId(TestIds.agentEditor.displayName).blur();
      await pages.agentEditor.prepareIncompleteLabel();
      await pages.agentEditor.refresh();
      await pages.agentEditor.expectIncompleteLabel();
      await expect(page.getByTestId(TestIds.agentEditor.displayName)).toHaveValue('Discard this draft');
      await pages.agentEditor.openOverview();
      await page.getByTestId('agent-discard').click();
      await pages.agentEditor.openEdit();
      await pages.agentEditor.expectNoLabelDraft();
      await expect(page.getByTestId(TestIds.agentEditor.displayName)).toHaveValue('Template saved from overview');
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      await pages.agentEditor.expectDeclaredSectionPresentation(variant.locale);
      await pages.agentEditor.openEdit();
      await page.evaluate(() => window.scrollTo(0, 0));
      await testInfo.attach('agent-template-identity', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });
      await pages.agentEditor.expectLocalizedInlineValidation(variant.locale);
      await pages.agentEditor.openTools();
      await expect(page.locator('.required-fields-note')).toHaveCount(0);
      await page.evaluate(() => window.scrollTo(0, 0));
      if (!variant.touch) {
        const panel = await page.getByTestId(TestIds.agentEditor.toolSetConfiguration).boundingBox();
        const navigation = await page.locator('.resource-details-navigation').boundingBox();
        expect(Math.abs(panel!.y - navigation!.y)).toBeLessThanOrEqual(2);
        await pages.agentEditor.openOverview();
        await page.evaluate(() => window.scrollTo(0, 400));
        const floating = await page.locator('.resource-details-navigation').boundingBox();
        expect(floating!.y).toBeGreaterThanOrEqual(64);
        expect(floating!.y).toBeLessThanOrEqual(96);
        await pages.agentEditor.openTools();
      }
      await expect(page.getByTestId(TestIds.agentEditor.toolConfiguration)).toBeVisible();
    });
  });
}
