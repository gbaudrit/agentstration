import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { test } from 'node:test';
import { repositoryRoot } from '../fixtures/repository.js';

const source = await fs.readFile(path.join(repositoryRoot, 'src/Agentstration.Web.Components/wwwroot/collection-ui.js'), 'utf8');
const module = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('late initialization after navigation tolerates missing and detached element references', () => {
  const browser = environment();
  for (const root of [null, undefined, { isConnected: false }]) {
    module.observeCollection(root, { invokeMethodAsync: () => assert.fail('Detached collection callback') });
    module.attachActions(root);
    module.attachDisclosure(root);
    module.releaseCollection(root);
    module.releaseActions(root);
    module.releaseDisclosure(root);
  }
  assert.equal(browser.listeners.size, 0);
});

test('replaced action and disclosure contents cannot crash the containing Blazor circuit', () => {
  const replaced = { isConnected: true, querySelector: () => null };
  module.attachActions(replaced);
  module.attachDisclosure(replaced);
});

function environment() {
  const listeners = new Set<EventListenerOrEventListenerObject>();
  const media = new EventTarget();
  let matches = false;
  globalThis.matchMedia = () => ({
    get matches() { return matches; },
    addEventListener: (_type: string, listener: EventListenerOrEventListenerObject) => { listeners.add(listener); media.addEventListener('change', listener); },
    removeEventListener: (_type: string, listener: EventListenerOrEventListenerObject) => { listeners.delete(listener); media.removeEventListener('change', listener); },
  }) as MediaQueryList;
  const window = new EventTarget();
  globalThis.window = window as unknown as Window & typeof globalThis;
  return { window, listeners, resize(value: boolean) { matches = value; media.dispatchEvent(new Event('change')); } };
}

test('collections follow the viewport until released, with no further callbacks after disposal', async () => {
  const browser = environment();
  const root = { isConnected: true };
  const calls: boolean[] = [];
  module.observeCollection(root, { invokeMethodAsync: async (method: string, narrow: boolean) => { assert.equal(method, 'SetNarrow'); calls.push(narrow); } });
  browser.resize(true);
  assert.deepEqual(calls, [false, true]);
  module.releaseCollection(root);
  browser.resize(false);
  assert.deepEqual(calls, [false, true]);
  assert.equal(browser.listeners.size, 0);
});

test('a callback interrupted by page navigation cannot reject into the next page', async () => {
  const browser = environment();
  const root = { isConnected: true };
  let reject!: (error: Error) => void;
  module.observeCollection(root, { invokeMethodAsync: () => new Promise<void>((_resolve, fail) => { reject = fail; }) });
  browser.window.dispatchEvent(new Event('pagehide'));
  reject(new Error("Cannot send data if the connection is not in the 'Connected' State."));
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(browser.listeners.size, 0);
  module.releaseCollection(root);
});

test('detaching a collection stops resize calls to a disposed .NET reference', () => {
  const browser = environment();
  const root = { isConnected: true };
  let calls = 0;
  module.observeCollection(root, { invokeMethodAsync: async () => { calls++; } });
  root.isConnected = false;
  browser.resize(true);
  assert.equal(calls, 1);
  assert.equal(browser.listeners.size, 0);
  module.releaseCollection(root);
});
