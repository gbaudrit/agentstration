import { exerciseScopedFlowDesigner } from '../src/journeys/exercise-scoped-flow-designer.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';

test.use({ authenticationMode: 'Local', actionTimeout: 15_000 });

test('Flow Designer catalogs, exact references, and publication feedback honor the Flow owner scope @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await exerciseScopedFlowDesigner({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('Flow Designer waits for step settings closure when Blazor rendering is delayed @smoke', async ({ page, product }) => {
  let delayedMessages = 0;
  // Inject transport latency into real Blazor renders to exercise the close/reopen race.
  await page.routeWebSocket(/\/_blazor(?:\?|$)/, socket => {
    const server = socket.connectToServer();
    const pending = new Set<ReturnType<typeof setTimeout>>();
    let closed = false;
    const stop = () => {
      closed = true;
      for (const timer of pending) clearTimeout(timer);
      pending.clear();
    };
    server.onMessage(message => {
      delayedMessages++;
      const timer = setTimeout(() => {
        pending.delete(timer);
        if (!closed) socket.send(message);
      }, 100);
      pending.add(timer);
    });
    socket.onClose(() => { stop(); server.close(); });
    server.onClose(() => { stop(); socket.close(); });
  });

  const pages = new ProductPages(page);
  await exerciseScopedFlowDesigner({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
  expect(delayedMessages).toBeGreaterThan(0);
});
