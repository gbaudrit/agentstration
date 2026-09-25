import { ProductPages } from '../src/pages/product.pages.js';
import { exerciseBootstrapProvenance } from '../src/journeys/exercise-bootstrap-provenance.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { test } from '../src/fixtures/test.js';

test('bootstrapped namespaced resources remain editable while Pack resources are read-only @responsive', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseBootstrapProvenance({ ...product, pages, checkpoint: ignoreCheckpoints }, {
    packPublisher: 'playwright',
    packName: 'bootstrap-provenance',
    packVersion: '0.1.0',
  });
});
