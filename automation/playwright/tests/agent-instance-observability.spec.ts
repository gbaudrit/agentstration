import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { inspectAgentInstanceObservability } from '../src/journeys/inspect-agent-instance-observability.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local' });
test.setTimeout(360_000);

test('Agent instances reflect effective execution, concurrency, cleanup, Worker loss, and authorization @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await inspectAgentInstanceObservability(
    { ...product, pages, checkpoint: ignoreCheckpoints },
    { verifyAuthorization: true },
  );
});

test('Agent instances remain usable with an active execution at mobile width @responsive @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await authenticateConsole({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  if (!product.runtimeWorker) throw new Error('The managed Runtime Worker fixture is unavailable.');
  await pages.runtimeWorkerObservability.ensureExecutionFixture(product.consoleUrl);
  product.runtimeWorker.blockNextModelInvocations(1);
  const runId = await pages.agentInstanceObservability.createRuntimeRun(product.consoleUrl, 'Keep the mobile Agent instance active.');
  try {
    await product.runtimeWorker.waitForBlockedModelInvocations();
    const active = await pages.agentInstanceObservability.waitForRunInstances(product.consoleUrl, [runId], 1);
    await pages.agentInstanceObservability.open(product.consoleUrl);
    await pages.agentInstanceObservability.expectInstances(active);
    await pages.agentInstanceObservability.expectResponsiveLayout();
  } finally {
    product.runtimeWorker.releaseBlockedModelInvocations();
  }
  await pages.runtimeWorkerObservability.waitForRuntimeRun(product.consoleUrl, runId, 'Succeeded');
});
