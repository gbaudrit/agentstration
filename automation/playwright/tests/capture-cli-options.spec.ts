import { expect, test } from '@playwright/test';
import { parseCaptureCliOptions, resolveCaptureAddresses } from '../src/capture/capture-cli-options.js';
import type { CapturePlan } from '../src/capture/capture-plan.js';

const plan: CapturePlan = {
  journey: 'create-agent',
  consoleUrl: 'https://plan-console.example.test/',
  workplaceUrl: 'https://plan-workplace.example.test/',
  captures: [{ checkpoint: 'agent-created', file: 'agent-created.png' }],
};

test('command-line URLs override capture-plan URLs', () => {
  const options = parseCaptureCliOptions([
    '--plan', 'capture.json',
    '--output', '.work/capture',
    '--console-url', 'https://cli-console.example.test/',
    '--workplace-url', 'https://cli-workplace.example.test/',
  ]);

  expect(resolveCaptureAddresses(options, plan)).toEqual({
    consoleUrl: 'https://cli-console.example.test',
    workplaceUrl: 'https://cli-workplace.example.test',
  });
});

test('a Console-only invocation does not require a Workplace URL', () => {
  const options = parseCaptureCliOptions([
    '--plan', 'capture.json',
    '--output', '.work/capture',
    '--console-url', 'https://cli-console.example.test',
  ]);

  expect(resolveCaptureAddresses(options, plan)).toEqual({
    consoleUrl: 'https://cli-console.example.test',
    workplaceUrl: 'https://cli-console.example.test',
  });
});
