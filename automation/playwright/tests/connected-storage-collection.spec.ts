import { test } from '../src/fixtures/test.js';
import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ConnectedStorageCollectionPage } from '../src/pages/connected-storage-collection.page.js';
import { ProductPages } from '../src/pages/product.pages.js';

test.use({ authenticationMode: 'Local', collectionCampaign: true });
for (const variant of [
  { name: 'desktop English dark', locale: 'en-US', theme: 'dark', width: 1440, height: 1000, touch: false, tag: '' },
  { name: 'mobile French light', locale: 'fr-FR', theme: 'light', width: 390, height: 844, touch: true, tag: '@responsive' },
] as const) {
  test.describe(variant.name, () => {
    test.use({ locale: variant.locale, colorScheme: variant.theme, hasTouch: variant.touch, viewport: { width: variant.width, height: variant.height } });
    test.afterEach(async ({ page, product }) => {
      if (variant.locale !== 'en-US') await new ProductPages(page).consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
    });
    test(`new Storage and Connected Service collections retain the compact contract ${variant.tag} @collections @smoke`, async ({ page, product, context }, testInfo) => {
      const pages = new ProductPages(page);
      await authenticateConsole({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
      await pages.consoleAdministration.restoreLanguage(product.consoleUrl, variant.locale);
      await pages.ensureTheme(variant.theme);
      await context.grantPermissions(['clipboard-read', 'clipboard-write']);
      const collections = new ConnectedStorageCollectionPage(page);
      const fixture = await collections.prepare(product.consoleUrl);
      for (const surface of ['storages', 'types', 'services'] as const) {
        if (surface === 'storages') await collections.inspectStorages(product.consoleUrl, fixture, variant.locale, variant.touch);
        if (surface === 'types') await collections.inspectTypes(product.consoleUrl, fixture, variant.locale);
        if (surface === 'services') await collections.inspectServices(product.consoleUrl, fixture, variant.locale, variant.touch);
        await testInfo.attach(`compact-${surface}-details`, { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });
        await testInfo.attach(`compact-${surface}-list`, { body: await collections.captureList(product.consoleUrl, surface), contentType: 'image/png' });
      }
    });
  });
}
