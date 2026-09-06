import fs from 'node:fs/promises';
import path from 'node:path';
import { expect, test } from '@playwright/test';
import type { CapturePlan } from '../src/capture/capture-plan.js';
import { createScreenshotRecorder } from '../src/capture/screenshot-recorder.js';

test('captures keep sticky elements in document flow and restore the page afterwards', async ({ page }, testInfo) => {
  await page.setContent(`
    <main style="height: 1200px">
      <div data-testid="capture-target" style="height: 800px">
        <button data-testid="sticky-action" style="position: sticky; bottom: 0">Save</button>
      </div>
    </main>
  `);
  const outputDirectory = testInfo.outputPath('capture');
  const plan: CapturePlan = {
    journey: 'test',
    captures: [
      { checkpoint: 'sticky-target', file: 'sticky-target.png', scope: 'target' },
      { checkpoint: 'sticky-page', file: 'sticky-page.png', scope: 'page', fullPage: true },
    ],
  };
  const assets: Array<{ checkpoint: string; file: string; sha256: string }> = [];
  const recorder = createScreenshotRecorder(plan, outputDirectory, assets);

  await recorder({
    name: 'sticky-target',
    page,
    target: page.getByTestId('capture-target'),
  });

  await recorder({ name: 'sticky-page', page });

  await expect(page.getByTestId('sticky-action')).toHaveCSS('position', 'sticky');
  await expect(page.getByTestId('sticky-action')).not.toHaveAttribute('data-agentstration-capture-sticky');
  await expect.poll(async () => fs.stat(path.join(outputDirectory, 'sticky-target.png')).then(value => value.size)).toBeGreaterThan(0);
  await expect.poll(async () => fs.stat(path.join(outputDirectory, 'sticky-page.png')).then(value => value.size)).toBeGreaterThan(0);
  expect(assets).toHaveLength(2);
});
