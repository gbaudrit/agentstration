import { TestIds } from '../src/contracts/test-ids.js';
import { test } from '../src/fixtures/test.js';
import { SourceLifecycleFixtures } from '../src/fixtures/source-lifecycle.js';
import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { TransformationFunctionCollectionPage } from '../src/pages/transformation-function-collection.page.js';
import { CollectionLayoutPage } from '../src/pages/collection-layout.page.js';

test.use({ authenticationMode: 'Local', governedInventoryFixture: true, collectionLayoutFixture: true });
test.skip(Boolean(process.env.AGENTSTRATION_CONSOLE_URL), 'Requires the disposable managed collection layout seed; never provisions an external instance.');
let artifactsPrepared = false;
let artifactScope = '';
for (const width of [1440, 390]) for (const locale of ['en-US', 'fr-FR'] as const) for (const theme of ['light', 'dark'] as const) {
  test.describe(`layout ${width} ${locale} ${theme}`, () => {
    test.use({ locale, colorScheme: theme, hasTouch: width < 761, viewport: { width, height: width < 761 ? 844 : 1000 } });
    test(`reported collection regressions ${width < 761 ? '@responsive' : locale === 'fr-FR' ? '@locale' : ''} @collections @smoke`, async ({ page, product }, info) => {
      test.setTimeout(240_000);
      const pages = new ProductPages(page);
      await authenticateConsole({ ...product, pages, checkpoint: ignoreCheckpoints }, { username: 'admin', password: 'Fixture753!Admin' });
      await pages.consoleAdministration.restoreLanguage(product.consoleUrl, locale);
      await pages.ensureTheme(theme);
      if (!artifactScope) artifactScope = `/workspaces/${await page.getByTestId(TestIds.console.shell).getAttribute('data-workspace-id')}`;
      if (!artifactsPrepared) {
        const fixtures = new SourceLifecycleFixtures(page);
        for (let index = 1; index <= 3; index++) await fixtures.createDurableArtifact(product.consoleUrl, `layout-${index}`,
          `Artifact-${index}-analyse-détaillée-des-documents-long-production-records-with-preserved-identity-and-version.txt`);
        artifactsPrepared = true;
      }
      for (const family of ['artifacts', 'queries', 'functions'] as const) await new CollectionLayoutPage(page, family, artifactScope).inspect(product.consoleUrl, info);
      if (width === 1440 && locale === 'en-US' && theme === 'light') {
        const functions = new TransformationFunctionCollectionPage(page);
        for (const checkWidth of [760, 761, 1024, 1280, 1659, 1920]) {
          await page.setViewportSize({ width: checkWidth, height: 1000 });
          const geometry = await functions.inspectTable();
          await info.attach(`functions-width-${checkWidth}`, { body: JSON.stringify(geometry, null, 2), contentType: 'application/json' });
          await info.attach(`functions-table-width-${checkWidth}`, { body: await page.screenshot(), contentType: 'image/png' });
        }
      }
    });
  });
}
