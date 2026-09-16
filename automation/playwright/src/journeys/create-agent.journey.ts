import type { AgentDefinition } from '../pages/agent-editor.page.js';
import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export type CreateAgentInput = AgentDefinition & {
  username?: string;
  password?: string;
  workspaceName?: string;
};

export const createAgent: Journey<CreateAgentInput> = async (context, input) => {
  validate(input);
  await prepareConsoleJourney(context, input);
  await context.pages.agentEditor.openNew(context.consoleUrl);
  await context.checkpoint({
    name: Checkpoints.createAgent.formEmpty,
    page: context.pages.page,
    target: context.pages.agentEditor.form,
  });

  await context.pages.agentEditor.fillIdentity(input);
  await context.checkpoint({
    name: Checkpoints.createAgent.identityComplete,
    page: context.pages.page,
    target: context.pages.agentEditor.identitySection,
  });

  await context.pages.agentEditor.configureBehavior(input);
  await context.checkpoint({
    name: Checkpoints.createAgent.behaviorComplete,
    page: context.pages.page,
    target: context.pages.agentEditor.behaviorSection,
  });
  await context.checkpoint({
    name: Checkpoints.createAgent.readyToCreate,
    page: context.pages.page,
    target: context.pages.agentEditor.form,
  });

  await context.pages.agentEditor.createAndDeploy(input.name);
  await context.checkpoint({
    name: Checkpoints.createAgent.created,
    page: context.pages.page,
    target: context.pages.agentEditor.form,
  });
};

function validate(input: CreateAgentInput): void {
  for (const property of ['name', 'displayName', 'description', 'instructions'] as const) {
    if (!input[property]?.trim()) throw new Error(`Create agent journey input '${property}' is required.`);
  }
  for (const property of ['modelProfile', 'runtimeProfile'] as const) {
    const value = input[property];
    const candidates = typeof value === 'string' ? [value] : value;
    if (!candidates?.length || candidates.some(candidate => !candidate.trim())) {
      throw new Error(`Create agent journey input '${property}' requires at least one non-empty candidate.`);
    }
  }
}
