import { expect } from '@playwright/test';
import { Checkpoints } from '../contracts/checkpoints.js';
import { TestIds } from '../contracts/test-ids.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface InspectRuntimeWorkerObservabilityInput {
  username?: string;
  password?: string;
  workspaceName?: string;
  verifyAuthorization?: boolean;
}

export const inspectRuntimeWorkerObservability: Journey<InspectRuntimeWorkerObservabilityInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const control = context.runtimeWorker;
  if (!control) throw new Error('The Runtime Worker observability journey requires the managed deterministic Worker fixture.');

  const worker = context.pages.runtimeWorkerObservability;
  await worker.ensureExecutionFixture(context.consoleUrl);
  await worker.openInventory(context.consoleUrl);
  const originalSessionId = await worker.expectPresence(control.workerId, 'Online');
  await context.checkpoint({
    name: Checkpoints.runtimeWorkerObservability.inventoryOnline,
    page: context.pages.page,
    target: worker.workerRow(control.workerId),
  });

  const successfulRunId = await worker.createSuccessfulRuntimeRun(context.consoleUrl);
  const successfulFlowRunId = await worker.createSuccessfulFlowRun(context.consoleUrl);
  await worker.openWorker(context.consoleUrl, control.workerId);
  await worker.openAssignments();
  await worker.expectAssignment(successfulRunId, 'Succeeded');
  await worker.expectAssignment(successfulFlowRunId, 'Succeeded');
  await context.checkpoint({
    name: Checkpoints.runtimeWorkerObservability.successfulAssignments,
    page: context.pages.page,
    target: context.pages.page.getByTestId(TestIds.runtimeWorkerObservability.assignments),
  });

  await worker.openRuntimeRunPlacement(context.consoleUrl, successfulRunId, control.workerId);
  await context.checkpoint({
    name: Checkpoints.runtimeWorkerObservability.runPlacement,
    page: context.pages.page,
    target: context.pages.page.getByTestId(TestIds.runtimeWorkerObservability.placement),
  });
  await worker.openFlowRunPlacement(context.consoleUrl, successfulFlowRunId, control.workerId);
  await context.checkpoint({
    name: Checkpoints.runtimeWorkerObservability.flowRunPlacement,
    page: context.pages.page,
    target: context.pages.page.getByTestId(TestIds.runtimeWorkerObservability.placement),
  });

  control.delayNextModelInvocation(60_000);
  const workerLossRunId = await worker.createRuntimeRun(
    context.consoleUrl,
    'Keep this deterministic invocation active until the Worker-loss fixture stops the process.',
  );
  await control.waitForDelayedModelInvocation();
  await control.crash();
  try {
    await worker.waitForRuntimeRun(context.consoleUrl, workerLossRunId, 'Failed', 'worker_lost');
    await worker.waitForPresence(context.consoleUrl, control.workerId, 'Offline');
    await context.checkpoint({
      name: Checkpoints.runtimeWorkerObservability.workerOffline,
      page: context.pages.page,
      target: worker.workerRow(control.workerId),
    });

    await worker.openWorker(context.consoleUrl, control.workerId);
    await worker.openAssignments();
    await worker.expectAssignment(workerLossRunId, 'Failed');
    await worker.openAssignment(workerLossRunId);
    await worker.expectCurrentPlacement(control.workerId, 'Interrupted', 'worker_lost');
    await context.checkpoint({
      name: Checkpoints.runtimeWorkerObservability.workerLoss,
      page: context.pages.page,
      target: context.pages.page.getByTestId(TestIds.runtimeWorkerObservability.placement),
    });
  } finally {
    await control.start();
  }

  await worker.waitForPresence(context.consoleUrl, control.workerId, 'Online');
  const replacementSessionId = await worker.expectPresence(control.workerId, 'Online');
  expect(replacementSessionId).not.toBe(originalSessionId);
  await context.checkpoint({
    name: Checkpoints.runtimeWorkerObservability.workerReconnected,
    page: context.pages.page,
    target: worker.workerRow(control.workerId),
  });

  if (input.verifyAuthorization) {
    await worker.exerciseAuthorizationBoundaries(context.consoleUrl, successfulRunId);
    await worker.openInventory(context.consoleUrl);
    await context.checkpoint({
      name: Checkpoints.runtimeWorkerObservability.authorizationBoundaries,
      page: context.pages.page,
      target: context.pages.page.getByTestId(TestIds.runtimeWorkerObservability.inventory),
    });
  }
};
