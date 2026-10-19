import { expect } from '@playwright/test';
import { Checkpoints } from '../contracts/checkpoints.js';
import { resolveCampaignWorkspaceName } from '../fixtures/campaign-workspace.js';
import type { ExecutionResourceCounts, ToolRunOutput } from '../pages/tool-execution.page.js';
import type { JourneyContext } from './journey.js';
import { prepareConsoleJourney, type ConsoleJourneySetup } from './prepare-console.journey.js';

export interface ExerciseToolExecutionInput extends ConsoleJourneySetup {
  notificationToolName?: string;
}

export interface ToolExecutionEvidence {
  beforeSimulation: ExecutionResourceCounts;
  afterSimulation: ExecutionResourceCounts;
  afterRealExecution: ExecutionResourceCounts;
  simulationOutput: ToolRunOutput;
  realOutput: ToolRunOutput;
}

export async function exerciseToolExecution(
  context: JourneyContext,
  input: ExerciseToolExecutionInput,
): Promise<ToolExecutionEvidence> {
  await prepareConsoleJourney(context, input);
  const tools = context.pages.toolExecution;
  const notificationTool = input.notificationToolName ?? 'agentstration.work.notification.create';
  const workspaceName = resolveCampaignWorkspaceName(input.workspaceName) ?? 'default';

  await tools.open(context.consoleUrl, notificationTool);
  await context.checkpoint({ name: Checkpoints.toolExecution.overview, page: context.pages.page, target: tools.details });
  await tools.openExecution();
  await tools.assertSimulationReady();
  await context.checkpoint({ name: Checkpoints.toolExecution.simulationReady, page: context.pages.page, target: tools.runner });
  await tools.assertRawCannotOverrideDryRun();
  await tools.fillNotification('Playwright simulation', 'This notification must not be persisted.');

  const beforeSimulation = await tools.snapshotExecutionResources(context.consoleUrl, workspaceName);
  const simulationOutput = await tools.run();
  expect(simulationOutput).toMatchObject({
    dryRun: true,
    title: 'Playwright simulation',
    message: 'This notification must not be persisted.',
  });
  const afterSimulation = await tools.snapshotExecutionResources(context.consoleUrl, workspaceName);
  expect(afterSimulation).toEqual(beforeSimulation);
  await context.checkpoint({ name: Checkpoints.toolExecution.simulationResult, page: context.pages.page, target: tools.result });

  await tools.selectRealExecution();
  await tools.fillNotification('Playwright execution', 'This notification proves the real Tool output.');
  const realOutput = await tools.run();
  expect(realOutput.dryRun).toBe(false);
  expect(realOutput.notificationId).toMatch(/^[0-9a-f-]{36}$/i);
  const afterRealExecution = await tools.snapshotExecutionResources(context.consoleUrl, workspaceName);
  expect(afterRealExecution.notifications).toBe(beforeSimulation.notifications + 1);
  expect(afterRealExecution.workItems).toBe(beforeSimulation.workItems);
  expect(afterRealExecution.flowRuns).toBe(beforeSimulation.flowRuns);
  expect(afterRealExecution.runtimeRuns).toBe(beforeSimulation.runtimeRuns);
  await context.checkpoint({ name: Checkpoints.toolExecution.realResult, page: context.pages.page, target: tools.result });

  const fixtures = await tools.createNonSimulableTools(context.consoleUrl);
  await tools.open(context.consoleUrl, fixtures.executable);
  await tools.openExecution();
  await tools.assertSimulationUnavailable();
  await context.checkpoint({ name: Checkpoints.toolExecution.simulationUnavailable, page: context.pages.page, target: tools.runner });

  await tools.open(context.consoleUrl, fixtures.approvalRequired);
  await tools.openExecution();
  await tools.assertApprovalRequired();
  await context.checkpoint({ name: Checkpoints.toolExecution.approvalRequired, page: context.pages.page, target: tools.runner });

  return { beforeSimulation, afterSimulation, afterRealExecution, simulationOutput, realOutput };
}
