import { ProductPages } from '../src/pages/product.pages.js';
import { exerciseSchemaContractBindings } from '../src/journeys/exercise-schema-contract-bindings.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { test } from '../src/fixtures/test.js';

test('Flow, Agent, and ToolDefinition editors expose reusable contracts consistently @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseSchemaContractBindings({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});
