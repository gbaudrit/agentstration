import assert from 'node:assert/strict';
import net, { type AddressInfo, type Server } from 'node:net';
import test from 'node:test';
import { withReservedLoopbackPorts } from './reserved-ports.js';

async function listen(port = 0): Promise<Server> {
  const server = net.createServer(socket => socket.destroy());
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, '127.0.0.1', resolve);
  });
  return server;
}

async function close(server: Server): Promise<void> {
  await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
}

async function assertReleased(ports: number[]): Promise<void> {
  for (const port of new Set(ports)) await close(await listen(port));
}

test('all seven host ports stay distinct and unavailable to sibling fixture servers', async () => {
  const allocated: number[] = [];
  await withReservedLoopbackPorts(7, async ports => {
    allocated.push(...ports.map(port => port.port));
    assert.equal(new Set(allocated).size, 7);
    const siblings = await Promise.all([listen(), listen(), listen()]);
    try {
      for (const server of siblings) assert.ok(!allocated.includes((server.address() as AddressInfo).port));
      await assert.rejects(listen(ports[5].port), { code: 'EADDRINUSE' });
    } finally {
      await Promise.all(siblings.map(close));
    }
  });
  await assertReleased(allocated);
});

test('handoff and repeated release leave the started host listening', async () => {
  let unusedPort = 0;
  const host = await withReservedLoopbackPorts(2, async ports => {
    unusedPort = ports[1].port;
    await Promise.all([ports[0].release(), ports[0].release()]);
    return await listen(ports[0].port);
  });
  try {
    assert.ok(host.listening);
    await assertReleased([unusedPort]);
  } finally {
    await close(host);
  }
});

test('a real address collision retries with fresh reservations and releases the failed attempt', async () => {
  let starts = 0;
  const allocated: number[] = [];
  const result = await withReservedLoopbackPorts(2, async ports => {
    starts++;
    allocated.push(...ports.map(port => port.port));
    if (starts === 1) await listen(ports[0].port);
    return 'ready';
  });
  assert.equal(result, 'ready');
  assert.equal(starts, 2);
  await assertReleased(allocated);
});

test('persistent collisions fail after three attempts and release every reservation', async () => {
  let starts = 0;
  const allocated: number[] = [];
  await assert.rejects(withReservedLoopbackPorts(2, async ports => {
    starts++;
    allocated.push(...ports.map(port => port.port));
    await listen(ports[0].port);
  }), { code: 'EADDRINUSE' });
  assert.equal(starts, 3);
  await assertReleased(allocated);
});

test('other startup failures are preserved without retry and release unused ports', async () => {
  let starts = 0;
  const allocated: number[] = [];
  const failure = new Error('Host exited: bootstrap validation failed.');
  await assert.rejects(withReservedLoopbackPorts(2, async ports => {
    starts++;
    allocated.push(...ports.map(port => port.port));
    await ports[0].release();
    throw failure;
  }), error => error === failure);
  assert.equal(starts, 1);
  await assertReleased(allocated);
});
