import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { test } from 'node:test';
import { repositoryRoot } from '../fixtures/repository.js';

const source = await fs.readFile(path.join(repositoryRoot, 'src/Agentstration.Web.FlowDesigner/wwwroot/js/flow-canvas.js'), 'utf8');
const module = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);

test('late zoom attachment tolerates missing and detached canvas elements', () => {
  for (const root of [null, undefined, { isConnected: false, addEventListener: () => assert.fail('Detached listener') }]) {
    module.attachCtrlWheelZoom(root, { invokeMethodAsync: () => assert.fail('Detached callback') }).dispose();
  }
});

test('Ctrl wheel zoom batches deltas and ordinary wheel scrolling stays native', () => {
  const fixture = canvas();
  const registration = module.attachCtrlWheelZoom(fixture.root, fixture.reference);
  fixture.wheel(false, 30);
  assert.equal(fixture.frames.size, 0);
  fixture.wheel(true, 40);
  fixture.wheel(true, 20);
  assert.equal(fixture.frames.size, 1);
  fixture.flush();
  assert.deepEqual(fixture.calls, [['ApplyCtrlWheelZoom', 60]]);
  registration.dispose();
  assert.equal(fixture.listenerCount(), 0);
});

test('a detached canvas cannot call its disposed .NET owner from a queued frame', () => {
  const fixture = canvas();
  const registration = module.attachCtrlWheelZoom(fixture.root, fixture.reference);
  fixture.wheel(true, 20);
  fixture.root.isConnected = false;
  fixture.flush();
  assert.deepEqual(fixture.calls, []);
  registration.dispose();
});

test('disposing zoom removes its listener and cancels pending frames', () => {
  const fixture = canvas();
  const registration = module.attachCtrlWheelZoom(fixture.root, fixture.reference);
  fixture.wheel(true, 20);
  registration.dispose();
  assert.equal(fixture.listenerCount(), 0);
  assert.equal(fixture.frames.size, 0);
  assert.deepEqual(fixture.calls, []);
});

interface WheelInput { ctrlKey: boolean; deltaY: number; preventDefault(): void; stopPropagation(): void }
function canvas() {
  const frames = new Map<number, FrameRequestCallback>();
  const calls: [string, number][] = [];
  let sequence = 0;
  let listener: ((event: WheelInput) => void) | undefined;
  globalThis.requestAnimationFrame = callback => { frames.set(++sequence, callback); return sequence; };
  globalThis.cancelAnimationFrame = id => { frames.delete(id); };
  const root = {
    isConnected: true,
    addEventListener(type: string, handler: (event: WheelInput) => void, options: { passive: boolean }) {
      assert.equal(type, 'wheel'); assert.equal(options.passive, false); listener = handler;
    },
    removeEventListener(type: string, handler: (event: WheelInput) => void) {
      assert.equal(type, 'wheel'); assert.equal(handler, listener); listener = undefined;
    },
  };
  return {
    root, frames, calls, listenerCount: () => listener ? 1 : 0,
    reference: { invokeMethodAsync: async (method: string, delta: number) => { calls.push([method, delta]); } },
    wheel(ctrlKey: boolean, deltaY: number) {
      listener!({ ctrlKey, deltaY, preventDefault() { assert.ok(ctrlKey); }, stopPropagation() { assert.ok(ctrlKey); } });
    },
    flush() { const callbacks = [...frames.values()]; frames.clear(); for (const callback of callbacks) callback(0); },
  };
}
