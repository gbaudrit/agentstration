import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { test } from 'node:test';
import { repositoryRoot } from '../fixtures/repository.js';
const source = await fs.readFile(path.join(repositoryRoot, 'src/Agentstration.Web.Components/wwwroot/resource-navigation.js'), 'utf8');
const module = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
function environment() {
  const rootListeners = new Map<string, (event: any) => void>();
  const windowListeners = new Map<string, () => void>();
  const mediaListeners = new Map<string, () => void>();
  const calls: unknown[][] = [];
  const link = { dataset: { resourceSection: 'definition/tools' } };
  const rail = { contains: () => true };
  const root = { isConnected: true, contains: (value: unknown) => value === link,
    addEventListener: (name: string, action: (event: any) => void) => rootListeners.set(name, action),
    removeEventListener: (name: string) => rootListeners.delete(name), querySelector: () => rail };
  const media = { matches: false, addEventListener: (name: string, action: () => void) => mediaListeners.set(name, action), removeEventListener: (name: string) => mediaListeners.delete(name) };
  Object.assign(globalThis, { window: { location: { href: 'https://example.test/agents/a?namespace=demo&context=%2Finstance#yaml' }, matchMedia: () => media,
    addEventListener: (name: string, action: () => void) => windowListeners.set(name, action), removeEventListener: (name: string) => windowListeners.delete(name) }, document: { activeElement: {} } });
  const registration = module.observe(root, { invokeMethodAsync: async (...args: unknown[]) => { calls.push(args); } });
  return { root, link, rootListeners, windowListeners, mediaListeners, media, calls, registration };
}
test('plain activation is intercepted while browser modified/new-tab actions remain native', () => {
  const e = environment(); let prevented = 0;
  const event = { target: { closest: () => e.link }, button: 0, preventDefault: () => prevented++ };
  for (const modifier of ['ctrlKey', 'metaKey', 'altKey', 'shiftKey']) e.rootListeners.get('click')!({ ...event, [modifier]: true });
  e.rootListeners.get('click')!({ ...event, button: 1 });
  assert.equal(prevented, 0); assert.equal(e.calls.length, 0);
  e.rootListeners.get('click')!(event);
  assert.equal(prevented, 1); assert.deepEqual(e.calls, [['NavigateSection', 'definition/tools']]);
  e.registration.dispose();
});
test('hash and browser history changes forward the actual URL', () => {
  const e = environment(); e.windowListeners.get('hashchange')!(); e.windowListeners.get('popstate')!();
  assert.equal(e.calls.length, 2); assert.equal(e.calls[0][0], 'ObserveLocation');
  assert.match(String(e.calls[0][1]), /#yaml$/); e.registration.dispose();
});
test('responsive transitions report focus context without changing section identity', () => {
  const e = environment(); e.media.matches = true; e.mediaListeners.get('change')!();
  assert.deepEqual(e.calls, [['BreakpointChanged', true, true]]); e.registration.dispose();
});
test('disposal removes every listener and stale callbacks do not reach a disposed component', () => {
  const e = environment(); const changed = e.windowListeners.get('hashchange')!;
  e.registration.dispose(); changed();
  assert.equal(e.calls.length, 0); assert.equal(e.rootListeners.size + e.windowListeners.size + e.mediaListeners.size, 0);
  assert.equal(module.observe(null, {}), undefined);
});
