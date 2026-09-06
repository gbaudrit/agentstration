import { Checkpoints } from '../contracts/checkpoints.js';
import type { HandoffFlowDefinition } from '../pages/flow-editor.page.js';
import type { Journey } from './journey.js';

export type CreateHandoffFlowInput = HandoffFlowDefinition & {
  username?: string;
  password?: string;
  workspaceName?: string;
};

export const createHandoffFlow: Journey<CreateHandoffFlowInput> = async (context, input) => {
  validate(input);
  await context.pages.login.signIn(context.consoleUrl, input.username, input.password);
  await context.pages.ensureTheme(context.theme ?? 'dark');
  if (input.workspaceName) {
    await context.pages.organizationWorkspaces.open(context.consoleUrl);
    await context.pages.organizationWorkspaces.selectByName(input.workspaceName);
  }

  const editor = context.pages.flowEditor;
  await editor.openNew(context.consoleUrl);
  await context.checkpoint({ name: Checkpoints.createFlow.formInitial, page: context.pages.page, target: editor.form });
  await editor.fillIdentity(input);
  await context.checkpoint({ name: Checkpoints.createFlow.identityComplete, page: context.pages.page, target: editor.identitySection });
  await editor.chooseOrchestration();
  await editor.selectParticipants(input.participants);
  await context.checkpoint({ name: Checkpoints.createFlow.participantsComplete, page: context.pages.page, target: editor.participantsSection });
  await editor.configureHandoff(input);
  await context.checkpoint({ name: Checkpoints.createFlow.strategyComplete, page: context.pages.page, target: editor.strategySection });
  await editor.create(input.name);
  await context.checkpoint({ name: Checkpoints.createFlow.created, page: context.pages.page, target: editor.orchestrationEditor });
  await editor.publish(input.version);
  await context.checkpoint({ name: Checkpoints.createFlow.published, page: context.pages.page, target: editor.orchestrationEditor });
};

function validate(input: CreateHandoffFlowInput): void {
  for (const property of ['name', 'displayName', 'description', 'version', 'initialParticipant', 'terminationPhrase'] as const) {
    if (!String(input[property] ?? '').trim()) throw new Error(`Create handoff Flow input '${property}' is required.`);
  }
  if (input.participants.length < 2) throw new Error('A handoff Flow requires at least two participants.');
  if (!input.participants.includes(input.initialParticipant)) throw new Error('The initial participant must belong to the Flow.');
  if (input.routes.length === 0) throw new Error('A handoff Flow requires at least one route.');
  for (const route of input.routes) {
    if (!input.participants.includes(route.from) || !input.participants.includes(route.to) || route.from === route.to) {
      throw new Error(`Invalid handoff route '${route.from}' -> '${route.to}'.`);
    }
  }
  if (!Number.isInteger(input.maximumTurnsPerParticipant) || input.maximumTurnsPerParticipant < 1) {
    throw new Error('maximumTurnsPerParticipant must be a positive integer.');
  }
}
