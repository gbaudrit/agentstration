import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface SubmitWorkplacePromptInput {
  workspaceName?: string;
  dashboardName?: string;
  entryNamespace?: string;
  entryName: string;
  prompt: string;
  username?: string;
  password?: string;
}

export const submitWorkplacePrompt: Journey<SubmitWorkplacePromptInput> = async (context, input) => {
  validate(input);
  await prepareConsoleJourney(context, input);

  const workplace = context.pages.workplace;
  const canonical = input.workspaceName
    ? await workplace.openWorkspace(context.workplaceUrl, input.workspaceName)
    : await workplace.openRoot(context.workplaceUrl);
  const route = {
    workspaceName: input.workspaceName ?? canonical.workspaceName,
    dashboardName: input.dashboardName ?? canonical.dashboardName,
  };
  if (route.dashboardName !== canonical.dashboardName) await workplace.openDashboard(context.workplaceUrl, route);
  await context.checkpoint({ name: Checkpoints.workplace.home, page: context.pages.page, target: workplace.home });

  await workplace.openEntry(context.workplaceUrl, route, input.entryNamespace ?? 'default', input.entryName);
  await context.checkpoint({ name: Checkpoints.workplace.entryReady, page: context.pages.page });
  await workplace.submitPrompt(input.prompt);
  await context.checkpoint({ name: Checkpoints.workplace.requestSubmitted, page: context.pages.page, target: workplace.interaction });
  await workplace.waitForAgentResponse();
  await context.checkpoint({ name: Checkpoints.workplace.responseCompleted, page: context.pages.page, target: workplace.conversationThread });
};

function validate(input: SubmitWorkplacePromptInput): void {
  if (!input.entryName?.trim()) throw new Error("Workplace prompt input 'entryName' is required.");
  if (!input.prompt?.trim()) throw new Error("Workplace prompt input 'prompt' is required.");
  for (const property of ['workspaceName', 'dashboardName', 'entryNamespace'] as const) {
    if (input[property] !== undefined && !input[property]?.trim()) {
      throw new Error(`Workplace prompt input '${property}' must not be empty.`);
    }
  }
}
