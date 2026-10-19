import { defineConfig, devices } from '@playwright/test';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const dataDirectory = join(tmpdir(), 'agentstration-console-playwright', String(process.pid));

export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.mjs',
  fullyParallel: false,
  timeout: 120_000,
  retries: 0,
  use: {
    baseURL: 'http://localhost:5199',
    ...devices['Desktop Chrome'],
    ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}),
  },
  webServer: {
    command: 'dotnet run --project ../../src/Agentstration.Web --configuration Release --launch-profile http --no-build --no-restore --urls http://localhost:5199',
    url: 'http://localhost:5199/health',
    timeout: 180_000,
    reuseExistingServer: process.env.PLAYWRIGHT_REUSE_SERVER === '1',
    env: {
      Data__Directory: dataDirectory,
      Data__ControlPlanePath: join(dataDirectory, 'control-plane.db'),
      Data__WorkPlanePath: join(dataDirectory, 'work-plane.db'),
      Data__FlowPath: join(dataDirectory, 'flow-plane.db'),
      Logging__EventLog__LogLevel__Default: 'None',
      AI__Provider: 'Deterministic',
      Agentstration__ManagementApi__BaseAddress: 'http://localhost:5199/',
      Agentstration__RuntimeApi__BaseAddress: 'http://localhost:5199/',
      Agentstration__WorkApi__BaseAddress: 'http://localhost:5199/',
      Agentstration__FlowApi__BaseAddress: 'http://localhost:5199/',
    },
  },
});
