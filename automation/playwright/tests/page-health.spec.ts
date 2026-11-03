import { expect, test } from '@playwright/test';
import { isActionableAssetResponse, isActionableConsoleError, isActionableFirstPartyFailure } from '../src/health/page-health.js';

test('page health classifies actionable browser failures @smoke', () => {
  expect(isActionableConsoleError('warning', 'deprecated')).toBe(false);
  expect(isActionableConsoleError('error', 'Failed to load resource: the server responded with a status of 404 (Not Found)')).toBe(false);
  expect(isActionableConsoleError('error', 'Uncaught TypeError')).toBe(true);

  const origins = new Set(['http://127.0.0.1:53400']);
  expect(isActionableFirstPartyFailure('http://127.0.0.1:53400/api/resources', origins, 'net::ERR_CONNECTION_REFUSED')).toBe(true);
  expect(isActionableFirstPartyFailure('http://127.0.0.1:53400/api/resources', origins, 'net::ERR_ABORTED')).toBe(false);
  expect(isActionableFirstPartyFailure('https://example.com/telemetry', origins, 'net::ERR_CONNECTION_REFUSED')).toBe(false);
  expect(isActionableAssetResponse('http://127.0.0.1:53400/Console.bundle.scp.css', origins, 'stylesheet', 404)).toBe(true);
  expect(isActionableAssetResponse('http://127.0.0.1:53400/app.js', origins, 'script', 500)).toBe(true);
  expect(isActionableAssetResponse('http://127.0.0.1:53400/app.css', origins, 'stylesheet', 200)).toBe(false);
  expect(isActionableAssetResponse('http://127.0.0.1:53400/api/resources/missing', origins, 'fetch', 404)).toBe(false);
  expect(isActionableAssetResponse('https://example.com/theme.css', origins, 'stylesheet', 404)).toBe(false);
  expect(isActionableAssetResponse('invalid URL', origins, 'stylesheet', 404)).toBe(false);
});
