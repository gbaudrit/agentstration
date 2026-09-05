import { test as base } from '@playwright/test';
import { startProductHosts, type ProductHosts } from './product-hosts.js';

interface AgentstrationWorkerFixtures {
  product: ProductHosts;
}

export const test = base.extend<{}, AgentstrationWorkerFixtures>({
  product: [async ({}, use) => {
    const product = await startProductHosts();
    try {
      await use(product);
    } finally {
      await product.stop();
    }
  }, { scope: 'worker' }],
});

export { expect } from '@playwright/test';
