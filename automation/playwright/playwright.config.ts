import { defineConfig, type ReporterDescription } from '@playwright/test';

const reporters: ReporterDescription[] = [['list'], ['html', { open: 'never' }], ['json', { outputFile: 'test-results/results.json' }]];
if (process.env.GITHUB_ACTIONS === 'true') reporters.push(['github']);

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 120_000,
  expect: { timeout: 15_000 },
  reporter: reporters,
  use: {
    channel: process.env.AGENTSTRATION_PLAYWRIGHT_CHANNEL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'retain-on-failure',
    locale: 'en-US',
    colorScheme: 'dark',
    viewport: { width: 1440, height: 1000 },
  },
  projects: [
    {
      name: 'desktop-chromium',
      grepInvert: /@responsive|@locale/,
    },
    {
      name: 'mobile-chromium',
      grep: /@responsive/,
      use: { viewport: { width: 390, height: 844 } },
    },
    {
      name: 'locale-contracts',
      grep: /@locale/,
    },
  ],
  outputDir: 'test-results',
});
