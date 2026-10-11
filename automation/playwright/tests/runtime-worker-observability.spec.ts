import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { inspectRuntimeWorkerObservability } from '../src/journeys/inspect-runtime-worker-observability.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });
test.setTimeout(300_000);

test('Runtime Worker inventory, placement, loss history, and authorization are correlated end to end @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await inspectRuntimeWorkerObservability(
    { ...product, pages, checkpoint: ignoreCheckpoints },
    { verifyAuthorization: true },
  );
});

test('Runtime Worker inventory and details remain usable at mobile width @responsive @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await authenticateConsole({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  if (!product.runtimeWorker) throw new Error('The managed Runtime Worker fixture is unavailable.');
  await pages.runtimeWorkerObservability.openInventory(product.consoleUrl);
  await pages.runtimeWorkerObservability.expectPresence(product.runtimeWorker.workerId, 'Online');
  await pages.runtimeWorkerObservability.expectResponsiveLayout();
  await pages.runtimeWorkerObservability.openWorker(product.consoleUrl, product.runtimeWorker.workerId);
  await pages.runtimeWorkerObservability.openAssignments();
});
