import { test as base } from '@playwright/test';
import { resolveExternalProductAddresses } from './external-product.js';
import { startProductHosts, type ProductHosts } from './product-hosts.js';
import { isActionableConsoleError, isActionableFirstPartyFailure } from '../health/page-health.js';

interface AgentstrationWorkerFixtures {
  product: ProductHosts;
}

interface AgentstrationTestFixtures {
  pageHealth: void;
}

export const test = base.extend<AgentstrationTestFixtures, AgentstrationWorkerFixtures>({
  product: [async ({}, use) => {
    const external = resolveExternalProductAddresses(process.env);
    if (external) {
      await use({ ...external, stop: async () => {} });
      return;
    }

    const product = await startProductHosts();
    try {
      await use(product);
    } finally {
      await product.stop();
    }
  }, { scope: 'worker' }],
  pageHealth: [async ({ page, product }, use) => {
    const failures: string[] = [];
    const firstPartyOrigins = new Set([new URL(product.consoleUrl).origin, new URL(product.workplaceUrl).origin]);
    page.on('pageerror', error => failures.push(`Unhandled page error: ${error.message}`));
    page.on('console', message => {
      if (isActionableConsoleError(message.type(), message.text())) {
        failures.push(`Browser console error: ${message.text()}`);
      }
    });
    page.on('requestfailed', request => {
      const reason = request.failure()?.errorText ?? 'unknown failure';
      if (!isActionableFirstPartyFailure(request.url(), firstPartyOrigins, reason)) return;
      failures.push(`First-party request failed: ${request.method()} ${request.url()} (${reason})`);
    });
    await use();
    if (!page.isClosed()) {
      const fatalState = page.locator('#blazor-error-ui, [data-testid="fatal-ui-state"]').filter({ visible: true });
      if (await fatalState.count() > 0) failures.push('A fatal application error state is visible.');
    }
    if (failures.length > 0) throw new Error(`Page health guard detected actionable failures:\n${failures.join('\n')}`);
  }, { auto: true }],
});

export { expect } from '@playwright/test';
