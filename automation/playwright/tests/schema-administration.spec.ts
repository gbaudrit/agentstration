import { exerciseSchemaAdministration } from '../src/journeys/exercise-schema-administration.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });

test('Schema drafts, native editing, validation and immutable versions work end to end @smoke', async ({ page, product }) => {
  test.setTimeout(300_000);
  const pages = new ProductPages(page);
  await exerciseSchemaAdministration({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('Schema authoring remains usable on mobile @smoke @responsive', async ({ page, product }) => {
  test.setTimeout(300_000);
  const pages = new ProductPages(page);
  await exerciseSchemaAdministration({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  await pages.schemaAdministration.assertResponsive();
});
