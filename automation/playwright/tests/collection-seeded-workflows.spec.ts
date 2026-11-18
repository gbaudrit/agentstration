import { test, expect } from '../src/fixtures/test.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { withCollectionAssertions } from '../src/journeys/inspect-collections.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { inspectFlowObservability } from '../src/journeys/inspect-flow-observability.journey.js';
import { CollectionContractPage, type CollectionObservation } from '../src/pages/collection-contract.page.js';
import { createPackProject } from '../src/journeys/create-pack-project.journey.js';
import { importSource } from '../src/journeys/import-source.journey.js';
import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { Checkpoints } from '../src/contracts/checkpoints.js';

// The existing observability/Workplace journeys use the seeded Development topology.
test.use({ authenticationMode: 'Development', collectionCampaign: true });

test('populated collection contract: tasks, Flow executions, events and Agent runs @collections @smoke', async ({ page, product }, testInfo) => {
  test.setTimeout(420_000);
  const observations = await withCollectionAssertions(
    { ...product, pages: new ProductPages(page), checkpoint: async checkpoint => { await testInfo.attach(checkpoint.name, { body: await checkpoint.page.screenshot({ fullPage: true }), contentType: 'image/png' }); } }, inspectFlowObservability, {},
  );
  expect(observations.length).toBeGreaterThan(0);
  await testInfo.attach('collection-observations', { body: JSON.stringify(observations, null, 2), contentType: 'application/json' });
});

test('Workplace populated entry, conversation, task, activity and notifications share applicable contracts @collections @smoke', async ({ page, product, context }, testInfo) => {
  const pages = new ProductPages(page);
  const journeyContext = { ...product, pages, checkpoint: ignoreCheckpoints };
  await authenticateConsole(journeyContext, {});
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  const collections = new CollectionContractPage(page);
  const observations: CollectionObservation[] = [];
  const workspace = await pages.workplace.openRoot(product.workplaceUrl);
  const owner = await collections.authorizedWorkspaceOwner(product.consoleUrl, workspace.workspaceName);
  const record = async (name: string) => {
    observations.push(await collections.inspect('en-US'));
    const disclosed = await collections.assertWorkplaceOwnerNames(owner);
    await testInfo.attach(name, { body: await page.screenshot({ fullPage: true }), contentType: 'image/png' });
    if (disclosed) await collections.dismissWorkplaceOwnerDisclosure();
  };
  await record(Checkpoints.workplace.home);
  await pages.workplace.openEntry(product.workplaceUrl, workspace, 'default', 'prepare-report');
  const conversation = await pages.workplace.submitPrompt('Prepare a deterministic collection contract report.');
  const taskId = await pages.workplace.waitForInlineTask();
  await pages.workplace.waitForAgentResponse();
  await record(Checkpoints.workplace.responseCompleted);
  await pages.workplace.openActivity(product.workplaceUrl, workspace.workspaceName);
  await pages.workplace.waitForConversation(conversation.conversationId);
  await record(Checkpoints.collections.workplaceActivity);
  await pages.workplace.showTasks();
  await pages.workplace.waitForTask(taskId);
  await record(Checkpoints.collections.workplaceTasks);
  await pages.workplace.openTask(product.workplaceUrl, workspace.workspaceName, taskId);
  await record(Checkpoints.collections.workplaceTask);
  await pages.workplace.openNotifications(product.workplaceUrl, workspace.workspaceName);
  await record(Checkpoints.collections.workplaceNotifications);
  await pages.workplace.markFirstNotificationRead();
  await testInfo.attach('collection-observations', { body: JSON.stringify(observations, null, 2), contentType: 'application/json' });
});

test('populated collection contract: Pack resource selection and build history @collections @smoke', async ({ page, product }, testInfo) => {
  const observations = await withCollectionAssertions(
    { ...product, pages: new ProductPages(page), checkpoint: async checkpoint => { await testInfo.attach(checkpoint.name, { body: await checkpoint.page.screenshot({ fullPage: true }), contentType: 'image/png' }); } }, createPackProject,
    { publisher: 'playwright', name: 'collection-contract', version: '1.0.0', displayName: 'Collection contract' },
  );
  expect(observations.length).toBeGreaterThan(0);
  await testInfo.attach('collection-observations', { body: JSON.stringify(observations, null, 2), contentType: 'application/json' });
});

test('populated collection contract: Source versions, bindings and channels @collections @smoke', async ({ page, product }, testInfo) => {
  const observations = await withCollectionAssertions(
    { ...product, pages: new ProductPages(page), checkpoint: async checkpoint => { await testInfo.attach(checkpoint.name, { body: await checkpoint.page.screenshot({ fullPage: true }), contentType: 'image/png' }); } }, importSource,
    { publisher: 'playwright', name: 'collection-source', displayName: 'Collection source' },
  );
  expect(observations.length).toBeGreaterThan(0);
  await testInfo.attach('collection-observations', { body: JSON.stringify(observations, null, 2), contentType: 'application/json' });
});
