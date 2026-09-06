import { TestIds } from '../src/contracts/test-ids.js';
import { createAgent, type CreateAgentInput } from '../src/journeys/create-agent.journey.js';
import { createEntry, type CreateEntryInput } from '../src/journeys/create-entry.journey.js';
import { createHandoffFlow, type CreateHandoffFlowInput } from '../src/journeys/create-handoff-flow.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ExpectedTextByLocale } from '../src/locales/expected-text.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';

const agents: readonly CreateAgentInput[] = [
  {
    name: 'playwright-greeter',
    displayName: 'Playwright greeter',
    description: 'Routes the initial request.',
    instructions: 'Route each request to the relevant specialist.',
    modelProfile: 'default:reasoning-default',
    runtimeProfile: 'default:maf-builtin',
  },
  {
    name: 'playwright-solution',
    displayName: 'Playwright solution specialist',
    description: 'Answers product usage questions.',
    instructions: 'Answer product usage questions and hand off technical requests.',
    modelProfile: 'default:reasoning-default',
    runtimeProfile: 'default:maf-builtin',
  },
  {
    name: 'playwright-technical',
    displayName: 'Playwright technical specialist',
    description: 'Answers technical questions.',
    instructions: 'Answer technical questions and hand off integration requests.',
    modelProfile: 'default:reasoning-default',
    runtimeProfile: 'default:maf-builtin',
  },
  {
    name: 'playwright-integration',
    displayName: 'Playwright integration specialist',
    description: 'Answers integration questions.',
    instructions: 'Answer questions about tools and integrations.',
    modelProfile: 'default:reasoning-default',
    runtimeProfile: 'default:maf-builtin',
  },
];

const flow: CreateHandoffFlowInput = {
  name: 'playwright-handoff',
  displayName: 'Playwright handoff',
  description: 'Routes a conversation across a team of specialists.',
  version: '0.1.0',
  enabled: true,
  participants: agents.map(agent => agent.name),
  initialParticipant: agents[0]!.name,
  routes: agents.flatMap(source => agents
    .filter(target => target.name !== source.name)
    .map(target => ({ from: source.name, to: target.name }))),
  autonomous: true,
  maximumTurnsPerParticipant: 2,
  terminationPhrase: '[[DONE]]',
};

const entry: CreateEntryInput = {
  name: 'playwright-review',
  displayName: 'Playwright review',
  description: 'Starts a reviewed response in Workplace.',
  presentationKind: 'Prompt',
  placeholder: 'What should the team review?',
  icon: 'briefcase',
  participantsVisibility: 'Hidden',
  progressVisibility: 'Compact',
  taskDisplay: 'Auto',
  resultsDisplay: 'Auto',
  field: {
    name: 'request',
    label: 'Request',
    description: 'Describe the response to prepare.',
    placeholder: 'Describe your request',
    type: 'Prompt',
    required: true,
    primary: true,
  },
  targetFlow: flow.name,
  taskCreationMode: 'Automatic',
  allowConversation: true,
  streamResponse: true,
  suggestions: [
    { label: 'Multiple agents', value: 'How can several specialist agents collaborate?' },
    { label: 'Local execution', value: 'Can this experience use local models?' },
    { label: 'Internal tools', value: 'How can agents call internal tools?' },
  ],
};

test('an administrator can publish a handoff Flow and its Entry @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  for (const agent of agents) await createAgent(context, agent);

  await createHandoffFlow(context, flow);
  await expect(page.getByTestId(TestIds.flowEditor.statusMessage)).toHaveAttribute('data-published-version', flow.version);

  await createEntry(context, entry);
  await expect(page.getByTestId(TestIds.entryEditor.status)).toHaveAttribute('data-state', 'published');
  await expect(page.getByTestId(TestIds.entryEditor.publish)).toHaveAccessibleName(
    ExpectedTextByLocale['en-US'].entryEditor.publishPinnedVersion,
  );
});
