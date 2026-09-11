import { authenticateConsole } from './authenticate-console.journey.js';
import { createAgent, type CreateAgentInput } from './create-agent.journey.js';
import { createHandoffFlow, type CreateHandoffFlowInput } from './create-handoff-flow.journey.js';
import { createEntry, type CreateEntryInput } from './create-entry.journey.js';
import { createWorkspace, type CreateWorkspaceInput } from './create-workspace.journey.js';
import { submitWorkplacePrompt, type SubmitWorkplacePromptInput } from './submit-workplace-prompt.journey.js';
import { exposeEntryOnDashboard, type ExposeEntryOnDashboardInput } from './expose-entry-on-dashboard.journey.js';
import { importSource, type ImportSourceInput } from './import-source.journey.js';
import { createPackProject, type CreatePackProjectInput } from './create-pack-project.journey.js';
import { inspectFlowObservability, type InspectFlowObservabilityInput } from './inspect-flow-observability.journey.js';
import type { Journey } from './journey.js';

export const journeys: Readonly<Record<string, Journey<Record<string, unknown>>>> = {
  'authenticate-console': authenticateConsole as Journey<Record<string, unknown>>,
  'create-agent': (context, input) => createAgent(context, input as unknown as CreateAgentInput),
  'create-handoff-flow': (context, input) => createHandoffFlow(context, input as unknown as CreateHandoffFlowInput),
  'create-entry': (context, input) => createEntry(context, input as unknown as CreateEntryInput),
  'create-workspace': (context, input) => createWorkspace(context, input as unknown as CreateWorkspaceInput),
  'submit-workplace-prompt': (context, input) => submitWorkplacePrompt(context, input as unknown as SubmitWorkplacePromptInput),
  'expose-entry-on-dashboard': (context, input) => exposeEntryOnDashboard(context, input as unknown as ExposeEntryOnDashboardInput),
  'import-source': (context, input) => importSource(context, input as unknown as ImportSourceInput),
  'create-pack-project': (context, input) => createPackProject(context, input as unknown as CreatePackProjectInput),
  'inspect-flow-observability': (context, input) => inspectFlowObservability(context, input as unknown as InspectFlowObservabilityInput),
};
