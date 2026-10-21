import { randomBytes, randomUUID } from 'node:crypto';
import { spawn, type ChildProcessByStdio } from 'node:child_process';
import fs from 'node:fs/promises';
import { createWriteStream } from 'node:fs';
import { createServer, request as createRequest, type Server } from 'node:http';
import net from 'node:net';
import path from 'node:path';
import type { Readable } from 'node:stream';
import { automationRoot, repositoryRoot } from './repository.js';

export interface ProductAddresses {
  consoleUrl: string;
  workplaceUrl: string;
}

export interface ProductHosts extends ProductAddresses {
  platformHealth?: PlatformHealthTestControl;
  runtimeWorker?: RuntimeWorkerTestControl;
  stop(): Promise<void>;
}

export interface PlatformHealthTestControl {
  delayNextRuntimeStatus(milliseconds: number): void;
  setRuntimeUnavailable(unavailable: boolean): void;
}

export interface RuntimeWorkerTestControl {
  readonly workerId: string;
  blockNextModelInvocations(count: number): void;
  waitForBlockedModelInvocations(): Promise<void>;
  releaseBlockedModelInvocations(): void;
  delayNextModelInvocation(milliseconds: number): void;
  waitForDelayedModelInvocation(): Promise<void>;
  crash(): Promise<void>;
  stop(): Promise<void>;
  start(): Promise<void>;
}

interface ManagedProcess {
  child: ChildProcessByStdio<null, Readable, Readable>;
  output: string[];
  hasIndependentProcessGroup: boolean;
}

interface FakeOllama {
  url: string;
  stop(): Promise<void>;
}

interface ControlledRuntimeProxy extends PlatformHealthTestControl {
  url: string;
  stop(): Promise<void>;
}

interface ControlledWorkerAuthorityProxy {
  url: string;
  blockNextModelInvocations(count: number): void;
  waitForBlockedModelInvocations(): Promise<void>;
  releaseBlockedModelInvocations(): void;
  delayNextModelInvocation(milliseconds: number): void;
  waitForDelayedModelInvocation(): Promise<void>;
  stop(): Promise<void>;
}

const startupTimeoutMilliseconds = 180_000;

export async function startProductHosts(authenticationMode: 'Development' | 'Local' = 'Development'): Promise<ProductHosts> {
  const runId = `${Date.now()}-${process.pid}`;
  const workDirectory = path.join(automationRoot, '.work', runId);
  const dataDirectory = path.join(workDirectory, 'data');
  await fs.mkdir(dataDirectory, { recursive: true });

  const [consolePort, workplacePort, workerPort, extensionPort, gitExtensionPort, foundryExtensionPort, utilitiesExtensionPort] = await Promise.all([freePort(), freePort(), freePort(), freePort(), freePort(), freePort(), freePort()]);
  const consoleUrl = `http://127.0.0.1:${consolePort}`;
  const workplaceUrl = `http://127.0.0.1:${workplacePort}`;
  const workerUrl = `http://127.0.0.1:${workerPort}`;
  const extensionUrl = `http://127.0.0.1:${extensionPort}`;
  const gitExtensionUrl = `http://127.0.0.1:${gitExtensionPort}`;
  const foundryExtensionUrl = `http://127.0.0.1:${foundryExtensionPort}`;
  const utilitiesExtensionUrl = `http://127.0.0.1:${utilitiesExtensionPort}`;
  const bootstrapPath = path.join(workDirectory, 'bootstrap-profiles');
  await fs.cp(path.join(repositoryRoot, 'deploy', 'bootstrap', 'profiles'), bootstrapPath, { recursive: true });
  await fs.cp(path.join(automationRoot, 'fixtures', 'bootstrap-profiles'), bootstrapPath, { recursive: true });
  const runtimeProxy = await startControlledRuntimeProxy(consoleUrl);
  const workerAuthorityProxy = await startControlledWorkerAuthorityProxy(consoleUrl);
  const workerId = randomUUID();
  const credentialId = randomUUID();
  const workerKeyFile = path.join(workDirectory, 'runtime-worker.key');
  const utilitiesEnrollmentStateFile = path.join(workDirectory, 'utilities-extension.instance');
  await fs.writeFile(workerKeyFile, `${randomBytes(48).toString('base64')}\n`, { mode: 0o600 });

  const fakeOllama = await startFakeOllama();
  const modelExtension = runDotnet('src/Agentstration.Extensions.Ollama/Agentstration.Extensions.Ollama.csproj', path.join(workDirectory, 'model-extension.log'), {
    ASPNETCORE_ENVIRONMENT: 'Development',
    ASPNETCORE_URLS: extensionUrl,
    Logging__EventLog__LogLevel__Default: 'None',
    Ollama__Endpoint: fakeOllama.url,
  });
  const gitExtension = runDotnet('src/Agentstration.Extensions.Git/Agentstration.Extensions.Git.csproj', path.join(workDirectory, 'git-extension.log'), {
    ASPNETCORE_ENVIRONMENT: 'Development',
    ASPNETCORE_URLS: gitExtensionUrl,
    Logging__EventLog__LogLevel__Default: 'None',
    GitSourceProvider__AllowLocalRepositories: 'true',
  });
  const foundryExtension = runDotnet('src/Agentstration.Extensions.Foundry/Agentstration.Extensions.Foundry.csproj', path.join(workDirectory, 'foundry-extension.log'), {
    ASPNETCORE_ENVIRONMENT: 'Development',
    ASPNETCORE_URLS: foundryExtensionUrl,
    Logging__EventLog__LogLevel__Default: 'None',
  });
  const utilitiesExtension = runDotnet('src/Agentstration.Extensions.Utilities/Agentstration.Extensions.Utilities.csproj', path.join(workDirectory, 'utilities-extension.log'), {
    ASPNETCORE_ENVIRONMENT: 'Development',
    ASPNETCORE_URLS: utilitiesExtensionUrl,
    Logging__EventLog__LogLevel__Default: 'None',
    Aep__EnrollmentMode: 'PairingCode',
    Aep__PairingCode__AuthorityUrl: consoleUrl,
    Aep__PairingCode__PublicEndpoint: utilitiesExtensionUrl,
    Aep__PairingCode__StateFile: utilitiesEnrollmentStateFile,
    Aep__PairingCode__AllowInsecureHttp: 'true',
  });

  try {
    await Promise.all([
      waitUntilHealthy(`${extensionUrl}/health`, modelExtension),
      waitUntilHealthy(`${gitExtensionUrl}/health`, gitExtension),
      waitUntilHealthy(`${foundryExtensionUrl}/health`, foundryExtension),
      waitUntilHealthy(`${utilitiesExtensionUrl}/health`, utilitiesExtension),
    ]);
  } catch (error) {
    await stopProcess(modelExtension);
    await stopProcess(gitExtension);
    await stopProcess(foundryExtension);
    await stopProcess(utilitiesExtension);
    await fakeOllama.stop();
    await runtimeProxy.stop();
    throw error;
  }

  const consoleHost = runDotnet('src/Agentstration.Web/Agentstration.Web.csproj', path.join(workDirectory, 'console.log'), {
    ASPNETCORE_ENVIRONMENT: 'Development',
    ASPNETCORE_URLS: consoleUrl,
    Logging__EventLog__LogLevel__Default: 'None',
    Data__Directory: dataDirectory,
    Data__ControlPlanePath: path.join(dataDirectory, 'control-plane.db'),
    Data__WorkPlanePath: path.join(dataDirectory, 'work-plane.db'),
    Data__FlowPath: path.join(dataDirectory, 'flow-plane.db'),
    Data__RuntimePath: path.join(dataDirectory, 'runtime-plane.db'),
    AI__Provider: 'Deterministic',
    Agentstration__Authentication__Mode: authenticationMode,
    Agentstration__Authentication__DevelopmentGrantPlatformAdministrator: 'true',
    Agentstration__Bootstrap__Path: bootstrapPath,
    Agentstration__Bootstrap__InitialBootstrapEnabled: 'true',
    Agentstration__Bootstrap__InitialProfiles__0: 'development',
    Agentstration__ManagementApi__BaseAddress: `${consoleUrl}/`,
    Agentstration__RuntimeApi__BaseAddress: `${runtimeProxy.url}/`,
    Agentstration__WorkApi__BaseAddress: `${consoleUrl}/`,
    Agentstration__FlowApi__BaseAddress: `${consoleUrl}/`,
    Agentstration__WorkplaceBaseUrl: `${workplaceUrl}/`,
    Agentstration__Extensions__DiscoverOnStartup: 'true',
    Agentstration__AwpWorkerTrust__Enabled: 'true',
    Agentstration__AwpWorkerTrust__InstanceId: runId,
    Agentstration__AwpWorkerTrust__Credentials__0__WorkerId: workerId,
    Agentstration__AwpWorkerTrust__Credentials__0__CredentialId: credentialId,
    Agentstration__AwpWorkerTrust__Credentials__0__SharedKeyFile: workerKeyFile,
    Agentstration__RuntimeWorker__Lease__HeartbeatInterval: '00:00:01',
    Agentstration__RuntimeWorker__Lease__LeaseDuration: '00:00:06',
    Agentstration__RuntimeWorker__Lease__MinimumSideEffectLeaseRemaining: '00:00:01',
  }, [
    '--Agentstration:Extensions:Agentstration.Extensions.Ollama:RegistrationName=ollama-extension',
    `--Agentstration:Extensions:Agentstration.Extensions.Ollama:Endpoint=${extensionUrl}`,
    '--Agentstration:Extensions:Agentstration.Extensions.Git:RegistrationName=git-extension',
    `--Agentstration:Extensions:Agentstration.Extensions.Git:Endpoint=${gitExtensionUrl}`,
    '--Agentstration:Extensions:Agentstration.Extensions.Foundry:RegistrationName=foundry-extension',
    `--Agentstration:Extensions:Agentstration.Extensions.Foundry:Endpoint=${foundryExtensionUrl}`,
  ]);

  let workplaceHost: ManagedProcess | undefined;
  let workerHost: ManagedProcess | undefined;
  const startRuntimeWorker = async (): Promise<void> => {
    if (workerHost?.child.exitCode === null) return;
    workerHost = runDotnet('src/Agentstration.Runtime.Worker.MicrosoftAgentFramework/Agentstration.Runtime.Worker.MicrosoftAgentFramework.csproj', path.join(workDirectory, 'runtime-worker.log'), {
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: workerUrl,
      Logging__EventLog__LogLevel__Default: 'None',
      Agentstration__RuntimeWorker__AuthorityUrl: workerAuthorityProxy.url,
      Agentstration__RuntimeWorker__WorkerId: workerId,
      Agentstration__RuntimeWorker__CredentialId: credentialId,
      Agentstration__RuntimeWorker__InstanceId: runId,
      Agentstration__RuntimeWorker__SharedKeyFile: workerKeyFile,
      Agentstration__RuntimeWorker__AllowInsecureHttp: 'true',
      Agentstration__RuntimeWorker__ClaimWaitSeconds: '1',
      Agentstration__RuntimeWorker__LeaseSafetyMarginSeconds: '1',
      Agentstration__RuntimeWorker__MaximumConcurrentAssignments: '4',
    }, [], true);
    await waitUntilHealthy(`${workerUrl}/health/ready`, workerHost);
  };
  try {
    await waitUntilHealthy(`${consoleUrl}/health/ready`, consoleHost);
    await startRuntimeWorker();
    workplaceHost = runDotnet('src/Agentstration.Workplace.Web/Agentstration.Workplace.Web.csproj', path.join(workDirectory, 'workplace.log'), {
      ASPNETCORE_ENVIRONMENT: 'Development',
      ASPNETCORE_URLS: workplaceUrl,
      Logging__EventLog__LogLevel__Default: 'None',
      Agentstration__ApiBaseUrl: `${consoleUrl}/`,
      Agentstration__WorkplaceHubUrl: `${consoleUrl}/hubs/workplace`,
    });
    await waitUntilHealthy(`${workplaceUrl}/health`, workplaceHost);
  } catch (error) {
    await stopProcess(workplaceHost);
    await stopProcess(workerHost);
    await stopProcess(consoleHost);
    await stopProcess(modelExtension);
    await stopProcess(gitExtension);
    await stopProcess(foundryExtension);
    await stopProcess(utilitiesExtension);
    await fakeOllama.stop();
    await runtimeProxy.stop();
    await workerAuthorityProxy.stop();
    throw error;
  }

  return {
    consoleUrl,
    workplaceUrl,
    platformHealth: runtimeProxy,
    runtimeWorker: {
      workerId,
      blockNextModelInvocations: count => workerAuthorityProxy.blockNextModelInvocations(count),
      waitForBlockedModelInvocations: () => workerAuthorityProxy.waitForBlockedModelInvocations(),
      releaseBlockedModelInvocations: () => workerAuthorityProxy.releaseBlockedModelInvocations(),
      delayNextModelInvocation: milliseconds => workerAuthorityProxy.delayNextModelInvocation(milliseconds),
      waitForDelayedModelInvocation: () => workerAuthorityProxy.waitForDelayedModelInvocation(),
      async crash() {
        await crashProcess(workerHost);
        workerHost = undefined;
      },
      async stop() {
        await stopProcess(workerHost);
        workerHost = undefined;
      },
      start: startRuntimeWorker,
    },
    async stop() {
      await stopProcess(workplaceHost);
      await stopProcess(workerHost);
      await stopProcess(consoleHost);
      await stopProcess(modelExtension);
      await stopProcess(gitExtension);
      await stopProcess(foundryExtension);
      await stopProcess(utilitiesExtension);
      await fakeOllama.stop();
      await runtimeProxy.stop();
      await workerAuthorityProxy.stop();
    },
  };
}

async function startControlledWorkerAuthorityProxy(targetBaseUrl: string): Promise<ControlledWorkerAuthorityProxy> {
  let blockedInvocationsRemaining = 0;
  let blockedInvocationsObserved: Promise<void> = Promise.resolve();
  let blockedInvocationsObservedResolver: (() => void) | undefined;
  let blockedInvocationsRelease: Promise<void> = Promise.resolve();
  let blockedInvocationsReleaseResolver: (() => void) | undefined;
  let nextModelDelayMilliseconds = 0;
  let delayedInvocation: Promise<void> = Promise.resolve();
  let delayedInvocationObserved: (() => void) | undefined;
  const resetObservation = () => {
    delayedInvocation = new Promise(resolve => { delayedInvocationObserved = resolve; });
  };
  resetObservation();

  const server = createServer(async (incoming, outgoing) => {
    const target = new URL(incoming.url ?? '/', targetBaseUrl);
    const isModelInvocation = target.pathname === '/api/awp/v1/assignments/model/invoke'
      || target.pathname === '/api/awp/v1/assignments/model/stream';
    if (isModelInvocation && blockedInvocationsRemaining > 0) {
      blockedInvocationsRemaining--;
      if (blockedInvocationsRemaining === 0) blockedInvocationsObservedResolver?.();
      await cancellableWait(blockedInvocationsRelease, incoming, outgoing);
      if (incoming.destroyed || outgoing.destroyed) return;
    }
    if (isModelInvocation && nextModelDelayMilliseconds > 0) {
      const delay = nextModelDelayMilliseconds;
      nextModelDelayMilliseconds = 0;
      delayedInvocationObserved?.();
      await cancellableDelay(delay, incoming, outgoing);
      if (incoming.destroyed || outgoing.destroyed) return;
    }

    const headers = { ...incoming.headers, host: target.host };
    const forwarded = createRequest(target, { method: incoming.method, headers }, response => {
      outgoing.writeHead(response.statusCode ?? 502, response.headers);
      response.pipe(outgoing);
    });
    forwarded.on('error', error => {
      if (outgoing.headersSent) outgoing.destroy(error);
      else {
        outgoing.statusCode = 502;
        outgoing.end('Worker authority proxy unavailable.');
      }
    });
    incoming.pipe(forwarded);
  });
  const port = await listenOnLoopback(server);
  return {
    url: `http://127.0.0.1:${port}`,
    blockNextModelInvocations(count) {
      if (!Number.isInteger(count) || count < 1) throw new Error('The blocked model invocation count must be a positive integer.');
      if (blockedInvocationsRemaining > 0) throw new Error('A model invocation block is already active.');
      blockedInvocationsRemaining = count;
      blockedInvocationsObserved = new Promise(resolve => { blockedInvocationsObservedResolver = resolve; });
      blockedInvocationsRelease = new Promise(resolve => { blockedInvocationsReleaseResolver = resolve; });
    },
    waitForBlockedModelInvocations: async () => await withTimeout(
      blockedInvocationsObserved,
      30_000,
      'Timed out waiting for the blocked Worker model invocations.',
    ),
    releaseBlockedModelInvocations() {
      blockedInvocationsRemaining = 0;
      blockedInvocationsObservedResolver?.();
      blockedInvocationsReleaseResolver?.();
      blockedInvocationsObservedResolver = undefined;
      blockedInvocationsReleaseResolver = undefined;
    },
    delayNextModelInvocation(milliseconds) {
      nextModelDelayMilliseconds = Math.max(1, milliseconds);
      resetObservation();
    },
    waitForDelayedModelInvocation: async () => await withTimeout(
      delayedInvocation,
      30_000,
      'Timed out waiting for the delayed Worker model invocation.',
    ),
    stop: async () => await closeServer(server),
  };
}

async function withTimeout<T>(promise: Promise<T>, milliseconds: number, message: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      promise,
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error(message)), milliseconds);
      }),
    ]);
  } finally {
    if (timer) clearTimeout(timer);
  }
}

async function cancellableDelay(milliseconds: number, incoming: Readable, outgoing: import('node:http').ServerResponse): Promise<void> {
  await new Promise<void>(resolve => {
    const timer = setTimeout(finish, milliseconds);
    incoming.once('aborted', finish);
    outgoing.once('close', finish);
    function finish() {
      clearTimeout(timer);
      incoming.off('aborted', finish);
      outgoing.off('close', finish);
      resolve();
    }
  });
}

async function cancellableWait(wait: Promise<void>, incoming: Readable, outgoing: import('node:http').ServerResponse): Promise<void> {
  await Promise.race([
    wait,
    new Promise<void>(resolve => {
      incoming.once('aborted', resolve);
      outgoing.once('close', resolve);
    }),
  ]);
}

async function startControlledRuntimeProxy(targetBaseUrl: string): Promise<ControlledRuntimeProxy> {
  let nextDelayMilliseconds = 0;
  let unavailable = false;
  const server = createServer(async (incoming, outgoing) => {
    const requestUrl = incoming.url ?? '/';
    const isRuntimeStatusRequest = new URL(requestUrl, targetBaseUrl).pathname === '/api/runtime/runs';
    if (isRuntimeStatusRequest && unavailable) {
      outgoing.statusCode = 503;
      outgoing.setHeader('Content-Type', 'application/problem+json');
      outgoing.end(JSON.stringify({ title: 'Runtime API unavailable in browser fixture', status: 503 }));
      return;
    }
    if (isRuntimeStatusRequest && nextDelayMilliseconds > 0) {
      const delay = nextDelayMilliseconds;
      nextDelayMilliseconds = 0;
      await new Promise(resolve => setTimeout(resolve, delay));
    }

    const target = new URL(requestUrl, targetBaseUrl);
    const headers = { ...incoming.headers, host: target.host };
    const forwarded = createRequest(target, { method: incoming.method, headers }, response => {
      outgoing.writeHead(response.statusCode ?? 502, response.headers);
      response.pipe(outgoing);
    });
    forwarded.on('error', error => {
      if (outgoing.headersSent) outgoing.destroy(error);
      else {
        outgoing.statusCode = 502;
        outgoing.end('Runtime proxy unavailable.');
      }
    });
    incoming.pipe(forwarded);
  });
  const port = await listenOnLoopback(server);
  return {
    url: `http://127.0.0.1:${port}`,
    delayNextRuntimeStatus(milliseconds) {
      nextDelayMilliseconds = Math.max(0, milliseconds);
    },
    setRuntimeUnavailable(value) {
      unavailable = value;
    },
    stop: async () => await closeServer(server),
  };
}

async function startFakeOllama(): Promise<FakeOllama> {
  const server = createServer((request, response) => {
    if (request.method === 'GET' && request.url === '/') {
      response.setHeader('Content-Type', 'text/plain');
      response.end('Ollama is running');
      return;
    }
    response.setHeader('Content-Type', 'application/json');
    if (request.method === 'GET' && request.url === '/api/version') {
      response.end(JSON.stringify({ version: '0.0.0-browser-fixture' }));
      return;
    }
    if (request.method === 'GET' && request.url === '/api/tags') {
      response.end(JSON.stringify({
        models: ['qwen3:1.7b', 'deterministic'].map(name => ({
          name,
          model: name,
          modified_at: '2026-01-01T00:00:00Z',
          size: 1,
          digest: `sha256:browser-fixture-${name}`,
          details: { parameter_size: 'test', quantization_level: 'test' },
        })),
      }));
      return;
    }
    response.statusCode = 404;
    response.end(JSON.stringify({ error: 'Not found in browser fixture.' }));
  });
  const port = await listenOnLoopback(server);
  return {
    url: `http://127.0.0.1:${port}`,
    stop: async () => await closeServer(server),
  };
}

async function listenOnLoopback(server: Server): Promise<number> {
  return await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      if (!address || typeof address === 'string') {
        reject(new Error('Could not allocate the fake Ollama endpoint.'));
        return;
      }
      resolve(address.port);
    });
  });
}

async function closeServer(server: Server): Promise<void> {
  if (!server.listening) return;
  await new Promise<void>((resolve, reject) => server.close(error => error ? reject(error) : resolve()));
}

function runDotnet(
  project: string,
  logFile: string,
  environment: NodeJS.ProcessEnv,
  applicationArguments: string[] = [],
  independentProcessGroup = false,
): ManagedProcess {
  const argumentsList = [
    'run',
    '--project', project,
    '--configuration', process.env.AGENTSTRATION_PLAYWRIGHT_CONFIGURATION ?? 'Release',
    '--no-launch-profile',
  ];
  if (process.env.AGENTSTRATION_PLAYWRIGHT_NO_BUILD === 'true') argumentsList.push('--no-build', '--no-restore');
  if (applicationArguments.length > 0) argumentsList.push('--', ...applicationArguments);

  const child = spawn('dotnet', argumentsList, {
    cwd: repositoryRoot,
    detached: independentProcessGroup && process.platform !== 'win32',
    env: { ...process.env, ...environment },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const output: string[] = [];
  const log = createWriteStream(logFile, { flags: 'a' });
  const record = (chunk: Buffer) => {
    output.push(chunk.toString());
    if (output.length > 200) output.shift();
    log.write(chunk);
  };
  child.stdout.on('data', record);
  child.stderr.on('data', record);
  child.once('exit', () => log.end());
  return { child, output, hasIndependentProcessGroup: independentProcessGroup };
}

async function waitUntilHealthy(url: string, process: ManagedProcess): Promise<void> {
  const deadline = Date.now() + startupTimeoutMilliseconds;
  while (Date.now() < deadline) {
    if (process.child.exitCode !== null) {
      throw new Error(`Host exited with code ${process.child.exitCode}.\n${process.output.join('')}`);
    }
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch {
      // The host is still starting.
    }
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error(`Timed out waiting for ${url}.\n${process.output.join('')}`);
}

async function stopProcess(process: ManagedProcess | undefined): Promise<void> {
  if (!process || process.child.exitCode !== null) return;
  process.child.kill('SIGTERM');
  await Promise.race([
    new Promise<void>(resolve => process.child.once('exit', () => resolve())),
    new Promise<void>(resolve => setTimeout(resolve, 5_000)),
  ]);
  if (process.child.exitCode === null) process.child.kill('SIGKILL');
}

async function crashProcess(managedProcess: ManagedProcess | undefined): Promise<void> {
  if (!managedProcess || managedProcess.child.exitCode !== null) return;
  const exited = new Promise<void>(resolve => managedProcess.child.once('exit', () => resolve()));
  if (process.platform === 'win32') {
    await new Promise<void>((resolve, reject) => {
      const terminator = spawn('taskkill', ['/PID', `${managedProcess.child.pid}`, '/T', '/F'], { stdio: 'ignore' });
      terminator.once('error', reject);
      terminator.once('exit', () => resolve());
    });
  } else if (managedProcess.hasIndependentProcessGroup) {
    process.kill(-managedProcess.child.pid!, 'SIGKILL');
  } else {
    managedProcess.child.kill('SIGKILL');
  }
  await exited;
}

async function freePort(): Promise<number> {
  return await new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      if (!address || typeof address === 'string') {
        server.close();
        reject(new Error('Could not allocate a local port.'));
        return;
      }
      const { port } = address;
      server.close(error => error ? reject(error) : resolve(port));
    });
  });
}
