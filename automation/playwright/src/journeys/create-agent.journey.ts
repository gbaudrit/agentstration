import type { AgentDefinition } from '../pages/agent-editor.page.js';
import type { Journey } from './journey.js';

export type CreateAgentInput = AgentDefinition & {
  username?: string;
  password?: string;
};

export const createAgent: Journey<CreateAgentInput> = async (context, input) => {
  validate(input);
  await context.pages.login.signIn(context.consoleUrl, input.username, input.password);
  await context.pages.ensureTheme(context.theme ?? 'dark');
  await context.pages.agentEditor.openNew(context.consoleUrl);
  await context.checkpoint({
    name: 'agent-form-empty',
    page: context.pages.page,
    target: context.pages.page.getByTestId('agent-editor-form'),
  });

  await context.pages.agentEditor.fillIdentity(input);
  await context.checkpoint({
    name: 'agent-identity-complete',
    page: context.pages.page,
    target: context.pages.page.getByTestId('agent-identity-section'),
  });

  await context.pages.agentEditor.configureBehavior(input);
  await context.checkpoint({
    name: 'agent-ready-to-create',
    page: context.pages.page,
    target: context.pages.page.getByTestId('agent-editor-form'),
  });

  await context.pages.agentEditor.createAndDeploy(input.name);
  await context.checkpoint({
    name: 'agent-created',
    page: context.pages.page,
    target: context.pages.page.getByTestId('agent-editor-form'),
  });
};

function validate(input: CreateAgentInput): void {
  for (const property of ['name', 'displayName', 'description', 'instructions', 'modelProfile', 'runtimeProfile'] as const) {
    if (!input[property]?.trim()) throw new Error(`Create agent journey input '${property}' is required.`);
  }
}
