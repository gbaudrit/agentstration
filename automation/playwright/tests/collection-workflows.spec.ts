import { test, expect } from '../src/fixtures/test.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { withCollectionAssertions } from '../src/journeys/inspect-collections.journey.js';
import { ignoreCheckpoints, type Journey } from '../src/journeys/journey.js';
import { administerDataSources, projectKnowledgeSources } from '../src/journeys/source-lifecycle.journey.js';
import { administerGovernedToolSets } from '../src/journeys/administer-governed-toolsets.journey.js';
import { inspectFlowObservability } from '../src/journeys/inspect-flow-observability.journey.js';
import { exerciseOperationalTriggers } from '../src/journeys/exercise-operational-triggers.journey.js';
import { inspectConsoleAdministration } from '../src/journeys/inspect-console-administration.journey.js';
import { exerciseSchemaAdministration } from '../src/journeys/exercise-schema-administration.journey.js';
import { exerciseModelAdministration } from '../src/journeys/exercise-model-administration.journey.js';
import { createPackProject } from '../src/journeys/create-pack-project.journey.js';
import { CollectionContractPage } from '../src/pages/collection-contract.page.js';
import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';

test.use({ authenticationMode: 'Local' });

const cases: ReadonlyArray<{ name: string; run: Journey<Record<string, never>> }> = [
  { name: 'acquisition history and profile revisions', run: context => administerDataSources(context, {}) },
  { name: 'projections, input evidence and snapshots', run: context => projectKnowledgeSources(context, {}) },
  { name: 'ToolSets, provider groups and pinned Agent members', run: context => administerGovernedToolSets(context, {}) },
  { name: 'tasks, Flow executions, events and Agent runs', run: context => inspectFlowObservability(context, {}) },
  { name: 'Trigger occurrences and operational ownership', run: context => exerciseOperationalTriggers(context, {}) },
  { name: 'members, personal tokens and command results', run: async context => {
    await authenticateConsole(context, {});
    await context.pages.organizationWorkspaces.selectByName('default');
    await inspectConsoleAdministration(context, {});
  } },
  { name: 'Schema versions', run: context => exerciseSchemaAdministration(context, {}) },
  { name: 'provider models and Model Profile capabilities', run: context => exerciseModelAdministration(context, {
    providerName: 'collection-ollama', providerDisplayName: 'Collection Ollama',
    profileName: 'collection-model', profileDisplayName: 'Collection model',
    runtimeName: 'collection-runtime', runtimeDisplayName: 'Collection runtime',
  }) },
  { name: 'Pack resource selection and build history', run: context => createPackProject(context, {
    publisher: 'playwright', name: 'collection-contract', version: '1.0.0', displayName: 'Collection contract',
  }) },
];

for (const scenario of cases) {
  test.describe(scenario.name, () => {
  // Reuse the same seeded Development topology as the existing observability journey.
  test.use({ authenticationMode: scenario.name === 'tasks, Flow executions, events and Agent runs' ? 'Development' : 'Local' });
  test(`populated collection contract: ${scenario.name} @collections @smoke`, async ({ page, product }, testInfo) => {
    test.setTimeout(420_000);
    const observations = await withCollectionAssertions(
      { ...product, pages: new ProductPages(page), checkpoint: ignoreCheckpoints }, scenario.run, {},
    );
    expect(observations.length).toBeGreaterThan(0);
    await testInfo.attach('collection-observations', { body: JSON.stringify(observations, null, 2), contentType: 'application/json' });
  });
  });
}

test.describe('seeded Workplace collections', () => {
test.use({ authenticationMode: 'Development' });
test('Workplace populated entry, conversation, task, activity and notifications share applicable contracts @collections @smoke', async ({ page, product, context }, testInfo) => {
  const pages = new ProductPages(page);
  const journeyContext = { ...product, pages, checkpoint: ignoreCheckpoints };
  await authenticateConsole(journeyContext, {});
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  const collections = new CollectionContractPage(page);
  const observations = [];
  const workspace = await pages.workplace.openRoot(product.workplaceUrl);
  observations.push(await collections.inspect('en-US'));
  await pages.workplace.openEntry(product.workplaceUrl, workspace, 'default', 'prepare-report');
  const conversation = await pages.workplace.submitPrompt('Prepare a deterministic collection contract report.');
  const taskId = await pages.workplace.waitForInlineTask();
  await pages.workplace.waitForAgentResponse();
  observations.push(await collections.inspect('en-US'));
  await pages.workplace.openActivity(product.workplaceUrl, workspace.workspaceName);
  await pages.workplace.waitForConversation(conversation.conversationId);
  observations.push(await collections.inspect('en-US'));
  await pages.workplace.showTasks();
  await pages.workplace.waitForTask(taskId);
  observations.push(await collections.inspect('en-US'));
  await pages.workplace.openTask(product.workplaceUrl, workspace.workspaceName, taskId);
  observations.push(await collections.inspect('en-US'));
  await pages.workplace.openNotifications(product.workplaceUrl, workspace.workspaceName);
  observations.push(await collections.inspect('en-US'));
  await pages.workplace.markFirstNotificationRead();
  await testInfo.attach('collection-observations', { body: JSON.stringify(observations, null, 2), contentType: 'application/json' });
});
});
