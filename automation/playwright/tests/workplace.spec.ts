import { TestIds } from '../src/contracts/test-ids.js';
import { createEntry, type CreateEntryInput } from '../src/journeys/create-entry.journey.js';
import { authenticateConsole } from '../src/journeys/authenticate-console.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { exposeEntryOnDashboard } from '../src/journeys/expose-entry-on-dashboard.journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';

const formEntry: CreateEntryInput = {
  name: 'playwright-form-request',
  displayName: 'Playwright form request',
  description: 'Exercises the Workplace form presentation.',
  presentationKind: 'Form',
  placeholder: 'Describe the requested form outcome.',
  participantsVisibility: 'Hidden',
  progressVisibility: 'Compact',
  taskDisplay: 'Auto',
  resultsDisplay: 'Auto',
  field: {
    name: 'request',
    label: 'Request',
    description: 'Describe the outcome.',
    placeholder: 'A deterministic form request',
    type: 'Text',
    required: true,
    primary: true,
  },
  targetAgent: 'dotnet-expert',
  taskCreationMode: 'Never',
  allowConversation: true,
  streamResponse: true,
  suggestions: [],
};

test('Workplace aliases, prompt conversations, theme, and recent navigation are operational @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await authenticateConsole(context, {});

  const root = await pages.workplace.openRoot(product.workplaceUrl);
  expect(root).toEqual({ workspaceName: 'default', dashboardName: 'home' });
  await expect(pages.workplace.realtimeStatus).toHaveAttribute('data-connected', 'true');
  await pages.workplace.toggleTheme();

  expect(await pages.workplace.openWorkspace(product.workplaceUrl, root.workspaceName)).toEqual(root);
  await pages.workplace.openDashboard(product.workplaceUrl, root);
  await pages.workplace.openEntry(product.workplaceUrl, root, 'default', 'quick-answer');
  const conversation = await pages.workplace.submitPrompt('Confirm that this deterministic Workplace request is ready.');
  await pages.workplace.waitForAgentResponse();

  await pages.workplace.openConversation(product.workplaceUrl, root, conversation.conversationId);
  await pages.workplace.openActivity(product.workplaceUrl, root.workspaceName);
  await pages.workplace.waitForConversation(conversation.conversationId);
  await pages.workplace.openDashboard(product.workplaceUrl, root);
  await expect(pages.workplace.recentConversation(conversation.conversationId)).toBeVisible();
});

test('a Workplace task completes, appears in activity, opens, and emits a readable notification @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await authenticateConsole(context, {});

  const route = await pages.workplace.openRoot(product.workplaceUrl);
  await pages.workplace.openEntry(product.workplaceUrl, route, 'default', 'prepare-report');
  const conversation = await pages.workplace.submitPrompt('Prepare a deterministic browser coverage report.');
  const taskId = await pages.workplace.waitForInlineTask();
  await pages.workplace.waitForAgentResponse();

  await pages.workplace.openActivity(product.workplaceUrl, route.workspaceName);
  await pages.workplace.waitForConversation(conversation.conversationId);
  await pages.workplace.showTasks();
  await pages.workplace.waitForTask(taskId);
  await pages.workplace.openTask(product.workplaceUrl, route.workspaceName, taskId);
  await expect(page.getByTestId(TestIds.workplace.taskDetails)).toHaveAttribute('data-task-id', taskId);

  await pages.workplace.openNotifications(product.workplaceUrl, route.workspaceName);
  await expect(page.getByTestId(TestIds.workplace.notification).first()).toBeVisible();
  await pages.workplace.markFirstNotificationRead();
});

test('a published form Entry can be submitted from its dedicated Workplace route @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await createEntry(context, formEntry);
  await exposeEntryOnDashboard(context, { entryName: formEntry.name });

  const route = await pages.workplace.openRoot(product.workplaceUrl);
  await pages.workplace.openEntry(product.workplaceUrl, route, 'default', formEntry.name);
  await pages.workplace.submitForm(formEntry.field.name, 'Exercise the deterministic form submission.');
  await pages.workplace.waitForAgentResponse();
});

test('Workplace keeps its primary navigation usable at a mobile viewport @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await authenticateConsole(context, {});
  await page.setViewportSize({ width: 390, height: 844 });

  await pages.workplace.openRoot(product.workplaceUrl);
  await expect(pages.workplace.mobileAppBar).toBeVisible();
  await expect(page.getByTestId(TestIds.workplace.activityLink)).toBeVisible();
  await expect(page.getByTestId(TestIds.workplace.notificationsLink).filter({ visible: true })).toBeVisible();
});
