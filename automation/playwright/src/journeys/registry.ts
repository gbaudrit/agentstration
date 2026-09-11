import { authenticateConsole } from './authenticate-console.journey.js';
import { createAgent, type CreateAgentInput } from './create-agent.journey.js';
import { createHandoffFlow, type CreateHandoffFlowInput } from './create-handoff-flow.journey.js';
import { createEntry, type CreateEntryInput } from './create-entry.journey.js';
import { createWorkspace, type CreateWorkspaceInput } from './create-workspace.journey.js';
import type { Journey } from './journey.js';

export const journeys: Readonly<Record<string, Journey<Record<string, unknown>>>> = {
  'authenticate-console': authenticateConsole as Journey<Record<string, unknown>>,
  'create-agent': (context, input) => createAgent(context, input as unknown as CreateAgentInput),
  'create-handoff-flow': (context, input) => createHandoffFlow(context, input as unknown as CreateHandoffFlowInput),
  'create-entry': (context, input) => createEntry(context, input as unknown as CreateEntryInput),
  'create-workspace': (context, input) => createWorkspace(context, input as unknown as CreateWorkspaceInput),
};
