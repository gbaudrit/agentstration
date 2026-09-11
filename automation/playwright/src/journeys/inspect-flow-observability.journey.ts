import { Checkpoints } from '../contracts/checkpoints.js';
import { TestIds } from '../contracts/test-ids.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface InspectFlowObservabilityInput {
  prompt?: string;
  username?: string;
  password?: string;
  workspaceName?: string;
}

export const inspectFlowObservability: Journey<InspectFlowObservabilityInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);

  const workplace = context.pages.workplace;
  const route = await workplace.openRoot(context.workplaceUrl);
  await workplace.openEntry(context.workplaceUrl, route, 'default', 'prepare-report');
  await workplace.submitPrompt(input.prompt?.trim() || 'Prepare a deterministic Flow observability report.');
  const taskId = await workplace.waitForInlineTask();
  await workplace.waitForAgentResponse();

  const observability = context.pages.flowObservability;
  await observability.openTask(context.consoleUrl, taskId);
  await context.checkpoint({ name: Checkpoints.flowObservability.task, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.flowObservability.taskDetails) });
  await observability.openFirstTaskFlowRun(taskId);
  await context.checkpoint({ name: Checkpoints.flowObservability.taskRun, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.flowObservability.taskRunDetails) });
  await observability.open(context.consoleUrl, '/flow-runs', 'runs');
  await observability.openFirstFlowRun();
  await context.checkpoint({ name: Checkpoints.flowObservability.globalRun, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.flowObservability.runDetails) });
  await observability.createAgentRun(context.consoleUrl, 'dotnet-expert', 'Return a deterministic Agent Run for browser observability.');
  await observability.open(context.consoleUrl, '/agent-runs', 'agentRuns');
  await observability.openFirstAgentRun();
  await context.checkpoint({ name: Checkpoints.flowObservability.agentRun, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.flowObservability.agentRunner) });
  await observability.open(context.consoleUrl, '/run-events', 'runEvents');
  await context.checkpoint({ name: Checkpoints.flowObservability.events, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.flowObservability.runEvents) });
};
