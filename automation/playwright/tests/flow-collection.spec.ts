import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { FlowCollectionPage } from '../src/pages/flow-collection.page.js';
import { Checkpoints } from '../src/contracts/checkpoints.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local', governedInventoryFixture: true });

for (const variant of [
  { locale: 'en-US', theme: 'dark', mobile: false },
  { locale: 'en-US', theme: 'light', mobile: false },
  { locale: 'fr-FR', theme: 'dark', mobile: true },
  { locale: 'fr-FR', theme: 'light', mobile: true },
] as const) {
  test.describe(`${variant.locale} ${variant.theme}`, () => {
  test.use({ hasTouch: variant.mobile });
  test(`compact Flows align versions and paginate ${variant.locale} ${variant.theme} ${variant.mobile ? '@responsive' : ''} @smoke`, async ({ page, product, context }, testInfo) => {
    const pages = new ProductPages(page);
    await authenticateConsole({ ...product, pages, checkpoint: ignoreCheckpoints }, process.env.AGENTSTRATION_CONSOLE_URL ? {} : { username: 'admin', password: 'Fixture753!Admin' });
    const flows = new FlowCollectionPage(page);
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await pages.sharedForms.configure(product.consoleUrl, variant.locale, variant.theme);
    await pages.flowObservability.open(product.consoleUrl, '/flows?context=%2Finstance', 'flows');
    await pages.ensureTheme(variant.theme);
    await flows.assertCompactStructure(variant.locale);
    await flows.assertManagedPreview();
    const managedPath = testInfo.outputPath(`${Checkpoints.flowCollection.managed}.png`);
    await flows.capture(managedPath);
    await testInfo.attach(Checkpoints.flowCollection.managed, { path: managedPath, contentType: 'image/png' });
    const ids = await flows.seed(product.consoleUrl);
    try {
      await pages.sharedForms.configure(product.consoleUrl, variant.locale, variant.theme);
      await pages.flowObservability.open(product.consoleUrl, '/flows?context=%2Finstance', 'flows');
      await pages.ensureTheme(variant.theme);
      await flows.exercisePagination(product.consoleUrl, variant.locale);
      if (!variant.mobile) {
        await flows.table();
        await flows.assertVersionAlignmentAndDisclosure(ids[0], variant.locale, variant.mobile);
        await flows.table();
        const path = testInfo.outputPath(`${Checkpoints.flowCollection.table}.png`);
        await flows.capture(path);
        await testInfo.attach(Checkpoints.flowCollection.table, { path, contentType: 'image/png' });
      }
      await flows.cards();
      await flows.assertCompactStructure(variant.locale);
      await flows.assertVersionAlignmentAndDisclosure(ids[0], variant.locale, variant.mobile);
      await flows.cards();
      const path = testInfo.outputPath(`${Checkpoints.flowCollection.cards}.png`);
      await flows.capture(path);
      await testInfo.attach(Checkpoints.flowCollection.cards, { path, contentType: 'image/png' });
    } finally {
      await flows.remove(product.consoleUrl, ids);
      await pages.consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
    }
  });
  });
}
