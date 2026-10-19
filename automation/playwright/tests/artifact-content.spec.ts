import { inspectArtifactContent } from '../src/journeys/inspect-artifact-content.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });
test.setTimeout(240_000);

test('governed artifact content stays bounded, downloadable, authorized, and integrity checked @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await inspectArtifactContent({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('governed artifact content remains usable at mobile width @responsive @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await inspectArtifactContent({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});
