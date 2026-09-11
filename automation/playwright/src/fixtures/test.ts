import { test as base } from '@playwright/test';
import { resolveExternalProductAddresses } from './external-product.js';
import { startProductHosts, type ProductHosts } from './product-hosts.js';

interface AgentstrationWorkerFixtures {
  product: ProductHosts;
}

export const test = base.extend<{}, AgentstrationWorkerFixtures>({
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
});

export { expect } from '@playwright/test';
