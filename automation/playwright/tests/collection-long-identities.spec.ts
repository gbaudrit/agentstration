import { test } from '../src/fixtures/test.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { CollectionContractPage } from '../src/pages/collection-contract.page.js';

test.use({ authenticationMode: 'Local', hasTouch: true });

for (const variant of [
  { name: 'desktop dark', width: 1440, height: 1000, locale: 'en-US', theme: 'dark', tag: '' },
  { name: 'desktop light', width: 1440, height: 1000, locale: 'en-US', theme: 'light', tag: '' },
  { name: 'mobile French dark', width: 390, height: 844, locale: 'fr-FR', theme: 'dark', tag: '@responsive' },
  { name: 'mobile French light', width: 390, height: 844, locale: 'fr-FR', theme: 'light', tag: '@responsive' },
] as const) {
  test.describe(variant.name, () => {
    test.use({ viewport: { width: variant.width, height: variant.height }, colorScheme: variant.theme, locale: variant.locale });
    test.afterEach(async ({ page, product }) => {
      if (variant.locale !== 'en-US' || variant.theme !== 'dark')
        await new ProductPages(page).consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
    });
    test(`long owners and homonymous identities ${variant.tag} @collections @smoke`, async ({ page, product }, testInfo) => {
      const pages = new ProductPages(page);
      await authenticateConsole({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
      await pages.consoleAdministration.restoreLanguage(product.consoleUrl, variant.locale);
      await pages.ensureTheme(variant.theme);
      const collections = new CollectionContractPage(page);
      const fixture = await collections.prepareLongOwnerHomonyms(product.consoleUrl);
      await collections.inspectLongOwnerHomonyms(product.consoleUrl, fixture, variant.width < 761);
      await collections.inspect(variant.locale);
      await collections.showOwnerDisclosure(fixture.owner, variant.width < 761);
      await testInfo.attach('long-owner-disclosure', { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });
    });
  });
}
