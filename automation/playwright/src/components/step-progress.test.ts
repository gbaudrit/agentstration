import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { test } from 'node:test';
import { repositoryRoot } from '../fixtures/repository.js';

const source = await fs.readFile(path.join(repositoryRoot, 'src/Agentstration.Web.Components/wwwroot/step-progress.js'), 'utf8');
const module = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

function environment() {
  let frameId = 0;
  const frames = new Map<number, FrameRequestCallback>();
  globalThis.requestAnimationFrame = callback => { frames.set(++frameId, callback); return frameId; };
  globalThis.cancelAnimationFrame = id => { frames.delete(id); };
  const window = Object.assign(new EventTarget(), { innerHeight: 800 });
  globalThis.window = window as unknown as Window & typeof globalThis;
  let mutated!: MutationCallback;
  let resized!: ResizeObserverCallback;
  const observed = new Set<Element>();
  globalThis.MutationObserver = class {
    constructor(callback: MutationCallback) { mutated = callback; }
    observe() {}
    disconnect() {}
  } as unknown as typeof MutationObserver;
  globalThis.ResizeObserver = class {
    constructor(callback: ResizeObserverCallback) { resized = callback; }
    observe(node: Element) { observed.add(node); }
    unobserve(node: Element) { observed.delete(node); }
    disconnect() { observed.clear(); }
  } as unknown as typeof ResizeObserver;
  const panel = {
    dataset: { selectedStep: 'current' }, scrollTop: 0,
    getBoundingClientRect: () => ({ top: 100, bottom: 300 }),
    querySelector: () => row,
  };
  const tabs = {
    scrollTop: 0, getBoundingClientRect: () => ({ top: 100, bottom: 300 }),
    querySelector: () => tab,
  };
  let rowTop = 650, tabTop = 650;
  const row = { getBoundingClientRect: () => ({ top: rowTop - panel.scrollTop, bottom: rowTop + 40 - panel.scrollTop }) };
  const tab = { getBoundingClientRect: () => ({ top: tabTop - tabs.scrollTop, bottom: tabTop + 40 - tabs.scrollTop }) };
  const root = Object.assign(new EventTarget(), {
    dataset: { followActive: 'true', activeStep: 'current', progressKey: '1' },
    classList: { contains: () => true },
    style: new Map<string, string>() as unknown as CSSStyleDeclaration,
    querySelector: (selector: string) => selector === '.step-progress-layout' ? { getBoundingClientRect: () => ({ top: 100 }) }
      : selector === '[data-step-progress-tabs]' ? tabs : panel,
  });
  let available = '';
  root.style.getPropertyValue = () => available;
  root.style.setProperty = (_key, value) => { available = value ?? ''; };
  let pauses = 0;
  module.attach(root, { invokeMethodAsync: async () => { pauses++; } });
  const flush = () => { const callbacks = [...frames.values()]; frames.clear(); for (const callback of callbacks) callback(0); };
  const mutation = (attributes: string[] = []) => {
    mutated(attributes.map(attributeName => ({ target: root, attributeName })) as unknown as MutationRecord[], {} as MutationObserver);
  };
  const wheel = () => {
    const event = new Event('wheel');
    // A bubbled wheel originates inside either scroll pane.
    Object.defineProperty(event, 'target', { value: { closest: () => panel } });
    root.dispatchEvent(event);
  };
  const pointer = (control: boolean) => {
    const event = new Event('pointerdown');
    Object.defineProperty(event, 'target', { value: {
      closest: (selector: string) => selector.startsWith('.step-progress-panel') ? panel : control ? {} : null,
    } });
    root.dispatchEvent(event);
  };
  return { root, panel, tabs, row, observed, flush, mutation, wheel, pointer, pauses: () => pauses,
    moveContent() { rowTop += 200; tabTop += 200; },
    resize() { resized([], {} as ResizeObserver); },
    frameCount: () => frames.size };
}

test('following repositions both panes after content changes with unchanged progress identities', () => {
  const browser = environment();
  browser.flush();
  assert.ok(browser.panel.scrollTop > 0);
  assert.ok(browser.tabs.scrollTop > 0);
  const previous = { panel: browser.panel.scrollTop, tabs: browser.tabs.scrollTop };
  browser.moveContent();
  browser.mutation();
  browser.flush();
  assert.ok(browser.panel.scrollTop > previous.panel, 'Reusing the same resource ID must not suppress scroll correction.');
  assert.ok(browser.tabs.scrollTop > previous.tabs);
  assert.ok(browser.observed.has(browser.panel as unknown as Element));
  assert.ok(browser.observed.has(browser.row as unknown as Element));
  browser.panel.scrollTop = 0;
  browser.resize();
  browser.flush();
  assert.ok(browser.panel.scrollTop > 0, 'Pane resizing must reveal the existing target again.');
  module.detach(browser.root);
  assert.equal(browser.observed.size, 0);
});

test('following stays enabled through wheel and pointer events until the feature pauses it', () => {
  const browser = environment();
  browser.flush();
  browser.wheel();
  browser.pointer(false);
  browser.pointer(true);
  assert.equal(browser.pauses(), 0, 'Layout JavaScript must never issue a late pause callback.');
  browser.panel.scrollTop = browser.tabs.scrollTop = 0;
  browser.mutation();
  browser.flush();
  assert.ok(browser.panel.scrollTop > 0);
  assert.ok(browser.tabs.scrollTop > 0);
  browser.root.dataset.followActive = 'false';
  browser.panel.scrollTop = browser.tabs.scrollTop = 0;
  browser.mutation(['data-follow-active']);
  browser.flush();
  assert.equal(browser.panel.scrollTop, 0, 'An explicit feature-owned inspection pause remains effective.');
  browser.root.dataset.followActive = 'true';
  browser.mutation(['data-follow-active']);
  browser.flush();
  assert.ok(browser.panel.scrollTop > 0);
  assert.ok(browser.tabs.scrollTop > 0);
  module.detach(browser.root);
});
