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
  const entryListeners = new Map<string, (event: any) => void>();
  const documentListeners = new Map<string, () => void>();
  const calls: unknown[][] = [];
  const link = { dataset: { resourceSection: 'definition/tools' } };
  const rail = { contains: () => true };
  const root = { isConnected: true, contains: (value: unknown) => value === link,
    addEventListener: (name: string, action: (event: any) => void) => rootListeners.set(name, action),
    removeEventListener: (name: string) => rootListeners.delete(name), querySelector: () => rail };
  const media = { matches: false, addEventListener: (name: string, action: () => void) => mediaListeners.set(name, action), removeEventListener: (name: string) => mediaListeners.delete(name) };
  Object.assign(globalThis, { window: { navigation: { addEventListener: (name: string, action: (event: any) => void) => entryListeners.set(name, action), removeEventListener: (name: string) => entryListeners.delete(name) }, location: { href: 'https://example.test/agents/a?namespace=demo&context=%2Finstance#yaml' }, matchMedia: () => media,
    addEventListener: (name: string, action: () => void) => windowListeners.set(name, action), removeEventListener: (name: string) => windowListeners.delete(name) }, document: { activeElement: {}, addEventListener: (name: string, action: () => void) => documentListeners.set(name, action), removeEventListener: (name: string) => documentListeners.delete(name) } });
  const registration = module.observe(root, { invokeMethodAsync: async (...args: unknown[]) => { calls.push(args); } });
  return { root, link, rootListeners, windowListeners, mediaListeners, media, rail, entryListeners, documentListeners, calls, registration };
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
test('fragment pushes are observed while guarded traversals remain owned by Blazor', () => {
  const e = environment();
  e.entryListeners.get('currententrychange')!({ navigationType: 'traverse' });
  assert.equal(e.calls.length, 0);
  e.entryListeners.get('currententrychange')!({ navigationType: 'push' });
  assert.deepEqual(e.calls, [['ObserveLocation', 'https://example.test/agents/a?namespace=demo&context=%2Finstance#yaml']]);
  assert.equal(e.windowListeners.size, 0);
  e.registration.dispose();
  assert.equal(e.entryListeners.size, 0);
});
test('responsive transitions report focus context without changing section identity', () => {
  const e = environment(); e.media.matches = true; e.mediaListeners.get('change')!();
  assert.deepEqual(e.calls, [['BreakpointChanged', true, true]]); e.registration.dispose();
});

test('native hash pushes use guarded Blazor navigation before committing a history entry', () => {
  const e = environment(); let prevented = 0;
  const destination = 'https://example.test/agents/a?namespace=demo&context=%2Finstance#definition/tools';
  const event = { hashChange: true, cancelable: true, navigationType: 'push', destination: { url: destination }, preventDefault: () => prevented++ };
  e.entryListeners.get('navigate')!(event);
  assert.equal(prevented, 1);
  assert.deepEqual(e.calls, [['NavigateLocation', destination]]);
  for (const change of [{ navigationType: 'traverse' }, { hashChange: false }, { cancelable: false }])
    e.entryListeners.get('navigate')!({ ...event, ...change });
  assert.equal(prevented, 1);
  e.registration.dispose(); assert.equal(e.entryListeners.size, 0);
});
test('focus remembered before CSS hides the rail transfers to the mobile trigger', () => {
  const e = environment(); e.media.matches = true; e.documentListeners.get('focusin')!();
  e.rail.contains = () => false; e.media.matches = false; e.mediaListeners.get('change')!();
  assert.deepEqual(e.calls, [['BreakpointChanged', false, true]]);
  e.registration.dispose(); assert.equal(e.documentListeners.size, 0);
});
test('disposal removes every listener and stale callbacks do not reach a disposed component', () => {
  const e = environment(); const resized = e.mediaListeners.get('change')!;
  e.registration.dispose(); resized();
  assert.equal(e.calls.length, 0); assert.equal(e.rootListeners.size + e.windowListeners.size + e.mediaListeners.size, 0);
  assert.equal(module.observe(null, {}), undefined);
});
test('public Blazor navigation owns section history and cannot rewrite another resource/context', () => {
  let current = new URL('https://example.test/agents/a?namespace=demo&context=%2Finstance&view=old#overview');
  const state = { _index: 4, application: 'preserved' }; const replacements: unknown[] = [];
  const location = { get href() { return current.href; }, set hash(value: string) { current.hash = value; } };
  Object.assign(globalThis, { window: { location, Blazor: { navigateTo: (url: string, options: { replaceHistoryEntry: boolean; historyEntryState?: string }) => { assert.equal(options.replaceHistoryEntry, false); assert.equal(options.historyEntryState, 'owned-state'); current = new URL(url); } }, history: { state, replaceState: (value: unknown, title: string, url: string) => { assert.equal(value, state); replacements.push(url); current = new URL(url); } }, dispatchEvent: () => true } });
  const destination = 'https://example.test/agents/a?namespace=demo&context=%2Finstance#definition/tools';
  assert.equal(module.navigate(destination, false, 'owned-state'), destination);
  assert.equal(replacements.length, 1);
  assert.equal(module.navigate('https://example.test/agents/a?context=%2Ftenants%2Fother#yaml', false), destination);
  assert.equal(module.navigate('https://example.test/agents/b?namespace=demo&context=%2Finstance#yaml', true), destination);
});
test('normalization replaces the current entry through Blazor navigation', () => {
  let current = 'https://example.test/agents/a#invalid'; let navigations = 0;
  Object.assign(globalThis, { window: { location: { get href() { return current; } }, Blazor: { navigateTo: (url: string, options: { replaceHistoryEntry: boolean }) => { assert.equal(options.replaceHistoryEntry, true); current = url; navigations++; } }, history: { state: {}, replaceState: (_state: unknown, _title: string, value: string) => { current = value; } } } });
  assert.equal(module.navigate('https://example.test/agents/a#overview', true), 'https://example.test/agents/a#overview');
  assert.equal(navigations, 1);
});
test('selecting the current destination does not add duplicate history', () => {
  const current = 'https://example.test/agents/a#yaml';
  Object.assign(globalThis, { window: { location: { href: current }, Blazor: { navigateTo: () => assert.fail('duplicate navigation') } } });
  assert.equal(module.navigate(current, false), current);
});
test('query identity matches ASP.NET key casing/grouping without changing coordinate values', () => {
  for (const suffix of ['VIEW=old&tAb=ignored', 'opaque=a&other=x&Opaque=b&VIEW=old']) {
    let current = new URL('https://example.test/agents/a?scopeRef=Owner&context=%2Finstance&' + suffix + '#yaml');
    const retained = suffix.startsWith('opaque') ? '&opaque=a&opaque=b&other=x' : '';
    const target = 'https://example.test/agents/a?scopeRef=Owner&context=%2Finstance' + retained + '#overview';
    Object.assign(globalThis, { window: {
      location: { get href() { return current.href; } },
      history: { state: {}, replaceState: (_state: unknown, _title: string, value: string) => { current = new URL(value); } },
      Blazor: { navigateTo: (value: string) => { current = new URL(value); } }
    } });
    assert.equal(module.navigate(target, true), target);
    assert.equal(module.navigate(target.replace('scopeRef=Owner', 'scopeRef=owner'), false), target);
  }
});

const contextSource = await fs.readFile(path.join(repositoryRoot, 'src/Agentstration.Web.Components/wwwroot/context-selector.js'), 'utf8');
const contextModule = await import(`data:text/javascript;base64,${Buffer.from(contextSource).toString('base64')}`);
test('context normalization notifies Blazor while preserving fragment and application history state', () => {
  const calls: unknown[][] = [];
  Object.assign(globalThis, { window: { location: { href: 'https://example.test/agents/a?namespace=demo#definition/tools' },
    Blazor: { navigateTo: (...args: unknown[]) => calls.push(args) } } });
  contextModule.ensureContextUrl('/instance', 'draft-owner');
  assert.deepEqual(calls, [['https://example.test/agents/a?namespace=demo&context=%2Finstance#definition/tools', { replaceHistoryEntry: true, historyEntryState: 'draft-owner' }]]);
  window.location.href = 'https://example.test/agents/a?context=%2Finstance#yaml';
  contextModule.ensureContextUrl('/instance', 'draft-owner');
  assert.equal(calls.length, 1);
});
