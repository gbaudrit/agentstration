import { inspectPlatformHealth } from '../src/journeys/inspect-platform-health.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { test } from '../src/fixtures/test.js';

test('Platform health resolves on direct entry and Console navigation @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  await inspectPlatformHealth({ ...product, pages, checkpoint: ignoreCheckpoints }, {});
});

test('Platform health resolves when navigation occurs during refresh @smoke', async ({ page, product }) => {
  test.skip(!product.platformHealth, 'The deterministic delay control is available only with the managed local fixture.');
  const pages = new ProductPages(page);
  await pages.login.signIn(product.consoleUrl);
  await pages.platformHealth.expectOperational();

  product.platformHealth!.delayNextRuntimeStatus(2_000);
  await pages.platformHealth.open(product.consoleUrl, '/');
  await pages.platformHealth.expectConnecting();
  await pages.platformHealth.navigateToAgents();
  await pages.platformHealth.expectOperational();
});

test('Platform health exposes an unavailable authoritative API @smoke', async ({ page, product }) => {
  test.skip(!product.platformHealth, 'The deterministic failure control is available only with the managed local fixture.');
  const pages = new ProductPages(page);
  await pages.login.signIn(product.consoleUrl);
  await pages.platformHealth.expectOperational();

  product.platformHealth!.setRuntimeUnavailable(true);
  try {
    await pages.platformHealth.open(product.consoleUrl, '/agents');
    await pages.platformHealth.expectPartiallyUnavailable();
  } finally {
    product.platformHealth!.setRuntimeUnavailable(false);
  }
});
