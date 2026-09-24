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
  stop(): Promise<void>;
}

export interface PlatformHealthTestControl {
  delayNextRuntimeStatus(milliseconds: number): void;
  setRuntimeUnavailable(unavailable: boolean): void;
}

interface ManagedProcess {
  child: ChildProcessByStdio<null, Readable, Readable>;
  output: string[];
}

interface FakeOllama {
  url: string;
  stop(): Promise<void>;
}

interface ControlledRuntimeProxy extends PlatformHealthTestControl {
  url: string;
  stop(): Promise<void>;
}

const startupTimeoutMilliseconds = 180_000;

export async function startProductHosts(): Promise<ProductHosts> {
  const runId = `${Date.now()}-${process.pid}`;
  const workDirectory = path.join(automationRoot, '.work', runId);
  const dataDirectory = path.join(workDirectory, 'data');
  await fs.mkdir(dataDirectory, { recursive: true });

  const [consolePort, workplacePort, extensionPort, gitExtensionPort, foundryExtensionPort] = await Promise.all([freePort(), freePort(), freePort(), freePort(), freePort()]);
  const consoleUrl = `http://127.0.0.1:${consolePort}`;
  const workplaceUrl = `http://127.0.0.1:${workplacePort}`;
  const extensionUrl = `http://127.0.0.1:${extensionPort}`;
  const gitExtensionUrl = `http://127.0.0.1:${gitExtensionPort}`;
  const foundryExtensionUrl = `http://127.0.0.1:${foundryExtensionPort}`;
  const bootstrapPath = path.join(repositoryRoot, 'deploy', 'bootstrap', 'profiles');
  const runtimeProxy = await startControlledRuntimeProxy(consoleUrl);

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

  try {
    await Promise.all([
      waitUntilHealthy(`${extensionUrl}/health`, modelExtension),
      waitUntilHealthy(`${gitExtensionUrl}/health`, gitExtension),
      waitUntilHealthy(`${foundryExtensionUrl}/health`, foundryExtension),
    ]);
  } catch (error) {
    await stopProcess(modelExtension);
    await stopProcess(gitExtension);
    await stopProcess(foundryExtension);
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
    Agentstration__Authentication__Mode: 'Development',
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
  }, [
    '--Agentstration:Extensions:Agentstration.Extensions.Ollama:RegistrationName=ollama-extension',
    `--Agentstration:Extensions:Agentstration.Extensions.Ollama:Endpoint=${extensionUrl}`,
    '--Agentstration:Extensions:Agentstration.Extensions.Git:RegistrationName=git-extension',
    `--Agentstration:Extensions:Agentstration.Extensions.Git:Endpoint=${gitExtensionUrl}`,
    '--Agentstration:Extensions:Agentstration.Extensions.Foundry:RegistrationName=foundry-extension',
    `--Agentstration:Extensions:Agentstration.Extensions.Foundry:Endpoint=${foundryExtensionUrl}`,
  ]);

  let workplaceHost: ManagedProcess | undefined;
  try {
    await waitUntilHealthy(`${consoleUrl}/health/ready`, consoleHost);
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
    await stopProcess(consoleHost);
    await stopProcess(modelExtension);
    await stopProcess(gitExtension);
    await stopProcess(foundryExtension);
    await fakeOllama.stop();
    await runtimeProxy.stop();
    throw error;
  }

  return {
    consoleUrl,
    workplaceUrl,
    platformHealth: runtimeProxy,
    async stop() {
      await stopProcess(workplaceHost);
      await stopProcess(consoleHost);
      await stopProcess(modelExtension);
      await stopProcess(gitExtension);
      await stopProcess(foundryExtension);
      await fakeOllama.stop();
      await runtimeProxy.stop();
    },
  };
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

function runDotnet(project: string, logFile: string, environment: NodeJS.ProcessEnv, applicationArguments: string[] = []): ManagedProcess {
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
  return { child, output };
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
