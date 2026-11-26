import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { test } from 'node:test';
import { repositoryRoot } from '../fixtures/repository.js';
const source = await fs.readFile(path.join(repositoryRoot, 'src/Agentstration.Web.Components/wwwroot/preview-location.js'), 'utf8');
const module = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function environment(navigationApi = true) {
  const listeners = new Map<string, (event?: any) => void>();
  const entries = new Map<string, (event: any) => void>();
  const calls: unknown[][] = [];
  const location = { href: 'https://example.test/providers/p?context=%2Finstance#preview/Tool/%2Finstance/default/tool' };
  Object.assign(globalThis, { window: { location,
    navigation: navigationApi ? { addEventListener: (name: string, action: (event: any) => void) => entries.set(name, action), removeEventListener: (name: string) => entries.delete(name) } : undefined,
    addEventListener: (name: string, action: () => void) => listeners.set(name, action), removeEventListener: (name: string) => listeners.delete(name),
  } });
  const observer = module.observePreviewLocation({ invokeMethodAsync: async (...args: unknown[]) => { calls.push(args); } });
  return { listeners, entries, calls, location, observer };
}

test('accepted Blazor fragment pushes and replacements restore Preview without a hashchange', () => {
  const e = environment();
  e.entries.get('currententrychange')!({ navigationType: 'push' });
  assert.deepEqual(e.calls, [['RestoreBrowserLocation', e.location.href]]);
  e.location.href = 'https://example.test/providers/p?context=%2Finstance#overview';
  e.entries.get('currententrychange')!({ navigationType: 'replace' });
  assert.deepEqual(e.calls[1], ['RestoreBrowserLocation', e.location.href]);
  e.observer.dispose();
});

test('Navigation API traversals do not bypass the owning Blazor navigation guard', () => {
  const e = environment();
  e.entries.get('currententrychange')!({ navigationType: 'traverse' });
  e.listeners.get('hashchange')!(); e.listeners.get('popstate')!();
  assert.equal(e.calls.length, 0);
  e.observer.dispose();
});

test('native browser fragment/history events remain supported without the Navigation API', () => {
  const e = environment(false);
  e.listeners.get('hashchange')!(); e.listeners.get('popstate')!();
  assert.deepEqual(e.calls, [['RestoreBrowserLocation', e.location.href], ['RestoreBrowserLocation', e.location.href]]);
  e.observer.dispose();
});

test('native Preview pushes enter the guarded Blazor path before any content restoration', () => {
  const e = environment(); let prevented = 0;
  e.entries.get('navigate')!({ hashChange: true, cancelable: true, navigationType: 'push',
    destination: { url: e.location.href }, preventDefault: () => prevented++ });
  assert.equal(prevented, 1);
  assert.deepEqual(e.calls, [['NavigatePreviewLocation', e.location.href]]);
  e.observer.dispose();
});

test('native Preview interception is bounded to its live collection and does not duplicate another handler', () => {
  const e = environment();
  const event = { hashChange: true, cancelable: true, navigationType: 'push', destination: { url: e.location.href }, preventDefault: () => assert.fail('must remain native') };
  for (const change of [{ defaultPrevented: true }, { navigationType: 'traverse' }, { hashChange: false }, { cancelable: false },
    { destination: { url: e.location.href.replace('/providers/p?', '/agents/a?') } },
    { destination: { url: e.location.href.replace('context=%2Finstance', 'context=%2Ftenants%2Fother') } },
    { destination: { url: e.location.href.split('#')[0] + '#overview' } },
    { destination: { url: e.location.href + 'x'.repeat(2048) } }]) e.entries.get('navigate')!({ ...event, ...change });
  e.location.href = 'https://example.test/agents/a#overview';
  e.entries.get('navigate')!({ ...event, destination: { url: e.location.href.split('#')[0] + '#preview/Agent/%2Finstance/default/a' } });
  assert.equal(e.calls.length, 0);
  e.observer.dispose();
});

test('disposal releases observers and ignores callbacks already queued by the browser', () => {
  const e = environment();
  const native = e.listeners.get('hashchange')!;
  const accepted = e.entries.get('currententrychange')!;
  e.observer.dispose(); native(); accepted({ navigationType: 'push' });
  assert.equal(e.calls.length, 0);
  assert.equal(e.listeners.size + e.entries.size, 0);
});
