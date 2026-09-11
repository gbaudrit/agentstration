import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { inspectFlowObservability } from '../src/journeys/inspect-flow-observability.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test('Flow definitions, immutable views, designer, and direct runner render from local state @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await authenticateConsole(context, {});

  await pages.flowObservability.open(product.consoleUrl, '/flows', 'flows');
  await pages.flowObservability.open(product.consoleUrl, '/flows/universal-router', 'flowDetails');
  await pages.flowObservability.open(product.consoleUrl, '/namespaces/default/flows/universal-router', 'flowDetails');
  await pages.flowObservability.open(product.consoleUrl, '/flows/universal-router/designer', 'designer');
  await pages.flowObservability.open(product.consoleUrl, '/namespaces/default/flows/universal-router/designer', 'designer');
  await pages.flowObservability.open(product.consoleUrl, '/agents/dotnet-expert/run', 'agentRunner');
});

test('a completed Workplace task is traceable through Flow, Agent, task, and event observability @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await inspectFlowObservability({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});
