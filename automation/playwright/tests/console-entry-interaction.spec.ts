import { createEntry, type CreateEntryInput } from '../src/journeys/create-entry.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { ProductPages } from '../src/pages/product.pages.js';
import { expect, test } from '../src/fixtures/test.js';

const entry: CreateEntryInput = {
  name: 'playwright-console-fallback',
  displayName: 'Playwright Console fallback',
  description: 'Exercises the generic Console Entry interaction.',
  presentationKind: 'Prompt',
  placeholder: 'Ask the deterministic Console Entry.',
  exposeConsole: true,
  consoleFallback: true,
  participantsVisibility: 'Hidden',
  progressVisibility: 'Compact',
  taskDisplay: 'Auto',
  resultsDisplay: 'Auto',
  field: {
    name: 'request',
    label: 'Request',
    description: 'Describe the request.',
    placeholder: 'A deterministic Console request',
    type: 'Prompt',
    required: true,
    primary: true,
  },
  targetAgent: 'dotnet-expert',
  taskCreationMode: 'Never',
  allowConversation: true,
  streamResponse: true,
  suggestions: [],
};

test('a command fallback opens and executes the generic Console Entry interaction @smoke', async ({ page, product }) => {
  const pages = new ProductPages(page);
  const context = { ...product, pages, checkpoint: ignoreCheckpoints };
  await createEntry(context, entry);

  const query = 'Explain the deterministic quokka integration path.';
  await pages.consoleEntryInteraction.startFromFallback(query, entry.displayName);
  await expect(pages.consoleEntryInteraction.interaction).toHaveAttribute('data-entry-name', entry.name);
  await expect(pages.consoleEntryInteraction.userMessage(query)).toBeVisible();
  await expect(pages.consoleEntryInteraction.assistantResponse).toBeVisible();

  await pages.consoleEntryInteraction.resumeFromConversations(query);
  await expect(pages.consoleEntryInteraction.interaction).toHaveAttribute('data-entry-name', entry.name);
  await expect(pages.consoleEntryInteraction.userMessage(query)).toBeVisible();
  await expect(pages.consoleEntryInteraction.assistantResponse).toBeVisible();
});
