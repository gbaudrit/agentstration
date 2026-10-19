import { execFile } from 'node:child_process';
import fs from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import { chromium } from '@playwright/test';
import { parseCaptureCliOptions, resolveCaptureAddresses } from '../src/capture/capture-cli-options.js';
import { readCapturePlan } from '../src/capture/capture-plan.js';
import { createScreenshotRecorder, type CapturedAsset } from '../src/capture/screenshot-recorder.js';
import { startProductHosts, type ProductHosts } from '../src/fixtures/product-hosts.js';
import { repositoryRoot } from '../src/fixtures/repository.js';
import { journeys } from '../src/journeys/registry.js';
import { ProductPages } from '../src/pages/product.pages.js';

const executeFile = promisify(execFile);
const options = parseCaptureCliOptions(process.argv.slice(2));
const outputDirectory = path.resolve(options.outputDirectory);
const plan = await readCapturePlan(path.resolve(options.planFile));
const journey = journeys[plan.journey];
if (!journey) throw new Error(`Unknown journey '${plan.journey}'. Available journeys: ${Object.keys(journeys).join(', ')}`);
const { stdout: headCommit } = await executeFile('git', ['rev-parse', 'HEAD'], { cwd: repositoryRoot });
const productCommit = headCommit.trim();
if (plan.productRef) {
  const { stdout: requestedCommit } = await executeFile('git', ['rev-parse', `${plan.productRef}^{commit}`], { cwd: repositoryRoot });
  if (requestedCommit.trim() !== productCommit) {
    throw new Error(`Capture plan requires ${plan.productRef} (${requestedCommit.trim()}) but the checkout is ${productCommit}.`);
  }
}

let product: ProductHosts | undefined;
const externalAddresses = resolveCaptureAddresses(options, plan);
const addresses = externalAddresses ?? (product = await startProductHosts());

const browser = await chromium.launch({
  headless: true,
  channel: process.env.AGENTSTRATION_PLAYWRIGHT_CHANNEL,
});
const assets: CapturedAsset[] = [];
try {
  const context = await browser.newContext({
    locale: plan.locale ?? 'en-US',
    colorScheme: plan.theme ?? 'dark',
    viewport: plan.viewport ?? { width: 1440, height: 1000 },
  });
  const page = await context.newPage();
  page.setDefaultTimeout(120_000);
  page.setDefaultNavigationTimeout(120_000);
  await journey({
    ...addresses,
    pages: new ProductPages(page),
    theme: plan.theme ?? 'dark',
    checkpoint: createScreenshotRecorder(plan, outputDirectory, assets),
  }, plan.input ?? {});
  await context.close();

  if (assets.length !== plan.captures.length) {
    const captured = new Set(assets.map(value => value.checkpoint));
    const missing = plan.captures.filter(value => !captured.has(value.checkpoint)).map(value => value.checkpoint);
    throw new Error(`Journey did not reach requested checkpoints: ${missing.join(', ')}`);
  }

  await fs.mkdir(outputDirectory, { recursive: true });
  const { stdout: status } = await executeFile('git', ['status', '--porcelain', '--untracked-files=no'], { cwd: repositoryRoot });
  const manifest = {
    productRef: plan.productRef,
    productCommit,
    productDirty: status.trim().length > 0,
    journey: plan.journey,
    playwrightVersion: (await import('@playwright/test/package.json', { with: { type: 'json' } })).default.version,
    browser: process.env.AGENTSTRATION_PLAYWRIGHT_CHANNEL ?? 'chromium',
    browserVersion: browser.version(),
    locale: plan.locale ?? 'en-US',
    theme: plan.theme ?? 'dark',
    viewport: plan.viewport ?? { width: 1440, height: 1000 },
    assets,
  };
  await fs.writeFile(path.join(outputDirectory, 'capture-manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
  console.log(JSON.stringify(manifest, null, 2));
} finally {
  await browser.close();
  await product?.stop();
}
