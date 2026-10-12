import { expect } from '@playwright/test';
import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface InspectAgentInstanceObservabilityInput {
  username?: string;
  password?: string;
  workspaceName?: string;
  verifyAuthorization?: boolean;
}

export const inspectAgentInstanceObservability: Journey<InspectAgentInstanceObservabilityInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const control = context.runtimeWorker;
  if (!control) throw new Error('The Agent instance observability journey requires the managed deterministic Worker fixture.');

  const instances = context.pages.agentInstanceObservability;
  const workers = context.pages.runtimeWorkerObservability;
  await workers.ensureExecutionFixture(context.consoleUrl);
  await instances.ensureMultiAgentFixture(context.consoleUrl);

  await instances.waitForNoInstances(context.consoleUrl);
  await instances.open(context.consoleUrl);
  await instances.expectEmpty();
  await context.checkpoint({
    name: Checkpoints.agentInstanceObservability.empty,
    page: context.pages.page,
    target: instances.emptyState,
  });

  control.blockNextModelInvocations(1);
  const directRunId = await instances.createRuntimeRun(context.consoleUrl, 'Keep the direct Agent instance active for observation.');
  try {
    await control.waitForBlockedModelInvocations();
    const active = await instances.waitForRunInstances(context.consoleUrl, [directRunId], 1);
    await instances.open(context.consoleUrl);
    await instances.expectInstances(active);
    await context.checkpoint({
      name: Checkpoints.agentInstanceObservability.directActive,
      page: context.pages.page,
      target: instances.instanceList,
    });

    await instances.followAgentLink(context.consoleUrl, active[0]);
    await instances.open(context.consoleUrl);
    await instances.followRunLink(context.consoleUrl, active[0]);
    await instances.open(context.consoleUrl);
    await instances.followWorkerLink(context.consoleUrl, active[0]);

    if (input.verifyAuthorization) {
      await instances.exerciseAuthorizationBoundaries(context.consoleUrl, active[0].workerId, directRunId);
    }
  } finally {
    control.releaseBlockedModelInvocations();
  }
  await workers.waitForRuntimeRun(context.consoleUrl, directRunId, 'Succeeded');
  await instances.waitForNoInstances(context.consoleUrl, [directRunId]);
  await instances.open(context.consoleUrl);
  await instances.expectEmpty();

  control.blockNextModelInvocations(2);
  const flowRunId = await instances.createFlowRun(context.consoleUrl);
  try {
    await control.waitForBlockedModelInvocations();
    const participants = await instances.waitForRunInstances(context.consoleUrl, [flowRunId], 2);
    expect(new Set(participants.map(instance => instance.agentName))).toEqual(new Set([
      'worker-observability-agent',
      'agent-instance-observability-peer',
    ]));
    await instances.open(context.consoleUrl);
    await instances.expectInstances(participants);
    await context.checkpoint({
      name: Checkpoints.agentInstanceObservability.flowParticipants,
      page: context.pages.page,
      target: instances.instanceList,
    });
    await instances.followRunLink(context.consoleUrl, participants[0]);
  } finally {
    control.releaseBlockedModelInvocations();
  }
  await instances.waitForFlowRun(context.consoleUrl, flowRunId, 'Succeeded');
  await instances.waitForNoInstances(context.consoleUrl, [flowRunId]);

  control.blockNextModelInvocations(2);
  const concurrentRunIds = [
    await instances.createRuntimeRun(context.consoleUrl, 'Keep concurrent Agent instance A active.'),
    await instances.createRuntimeRun(context.consoleUrl, 'Keep concurrent Agent instance B active.'),
  ];
  try {
    await control.waitForBlockedModelInvocations();
    const concurrent = await instances.waitForRunInstances(context.consoleUrl, concurrentRunIds, 2);
    expect(new Set(concurrent.map(instance => instance.instanceId)).size).toBe(2);
    expect(new Set(concurrent.map(instance => instance.runId))).toEqual(new Set(concurrentRunIds));
    expect(new Set(concurrent.map(instance => instance.agentName))).toEqual(new Set(['worker-observability-agent']));
    await instances.open(context.consoleUrl);
    await instances.expectInstances(concurrent);
    await context.checkpoint({
      name: Checkpoints.agentInstanceObservability.concurrentRuns,
      page: context.pages.page,
      target: instances.instanceList,
    });
  } finally {
    control.releaseBlockedModelInvocations();
  }
  await Promise.all(concurrentRunIds.map(runId => workers.waitForRuntimeRun(context.consoleUrl, runId, 'Succeeded')));
  await instances.waitForNoInstances(context.consoleUrl, concurrentRunIds);

  control.blockNextModelInvocations(1);
  const lostRunId = await instances.createRuntimeRun(context.consoleUrl, 'Keep this Agent instance active until its Worker is lost.');
  await control.waitForBlockedModelInvocations();
  await instances.waitForRunInstances(context.consoleUrl, [lostRunId], 1);
  await control.crash();
  try {
    await workers.waitForRuntimeRun(context.consoleUrl, lostRunId, 'Failed', 'worker_lost');
    await instances.waitForNoInstances(context.consoleUrl, [lostRunId]);
    await instances.open(context.consoleUrl);
    await instances.expectEmpty();
    await context.checkpoint({
      name: Checkpoints.agentInstanceObservability.workerLossCleared,
      page: context.pages.page,
      target: instances.emptyState,
    });
  } finally {
    control.releaseBlockedModelInvocations();
    await control.start();
  }
  await workers.waitForPresence(context.consoleUrl, control.workerId, 'Online');
};
