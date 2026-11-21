import net from 'node:net';

export interface LoopbackPortReservation {
  readonly port: number;
  release(): Promise<void>;
}

/** Keep sibling endpoints from reclaiming a host's port before it starts. */
export async function withReservedLoopbackPorts<T>(
  count: number,
  start: (ports: readonly LoopbackPortReservation[]) => Promise<T>,
): Promise<T> {
  if (!Number.isInteger(count) || count < 1) throw new RangeError('At least one port must be reserved.');
  for (let attempt = 1; ; attempt++) {
    const ports: LoopbackPortReservation[] = [];
    try {
      for (let index = 0; index < count; index++) ports.push(await reserveLoopbackPort());
      return await start(ports);
    } catch (error) {
      // Releasing a socket and starting another process cannot be atomic. Retry
      // only binding collisions; health timeouts and other failures still fail.
      if (attempt >= 3 || !(error instanceof Error)
          || !/EADDRINUSE|AddressInUseException|address already in use/i.test(error.message)) throw error;
      console.warn(`Local browser host port collision; retrying startup (${attempt}/3).`);
    } finally {
      await Promise.all(ports.map(port => port.release()));
    }
  }
}

async function reserveLoopbackPort(): Promise<LoopbackPortReservation> {
  const server = net.createServer(socket => socket.destroy());
  await new Promise<void>((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  server.unref();
  const address = server.address();
  if (!address || typeof address === 'string') {
    server.close();
    throw new Error('Could not reserve a local browser host port.');
  }
  let released: Promise<void> | undefined;
  return {
    port: address.port,
    release() {
      released ??= new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
      return released;
    },
  };
}
