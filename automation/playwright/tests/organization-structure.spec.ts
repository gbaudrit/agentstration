import { exerciseOrganizationStructure } from '../src/journeys/exercise-organization-structure.journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local', organizationStructureFixture: true });

for (const locale of ['en-US', 'fr-FR'] as const) {
  for (const mobile of [false, true]) {
    test(`organization structure ${locale} ${mobile ? 'mobile @responsive' : `desktop${locale === 'fr-FR' ? ' @locale' : ''}`} @smoke`, async ({ page, product }, testInfo) => {
      test.setTimeout(240_000);
      const pages = new ProductPages(page);
      const credentials = process.env.AGENTSTRATION_CONSOLE_URL ? {} : { username: 'admin', password: 'Fixture973!Admin' };
      let completed = false;
      try {
        await exerciseOrganizationStructure({ ...product, pages, checkpoint: async checkpoint => {
          const path = testInfo.outputPath(`${checkpoint.name}.png`);
          await checkpoint.page.screenshot({ path, fullPage: true });
          await testInfo.attach(checkpoint.name, { path, contentType: 'image/png' });
        } }, { locale, ...credentials });
        completed = true;
      } finally {
        if (completed) await pages.consoleAdministration.restoreLanguage(product.consoleUrl, 'en-US');
      }
    });
  }
}
