import { Checkpoints } from '../contracts/checkpoints.js';
import type { EntryDefinition } from '../pages/entry-editor.page.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export type CreateEntryInput = EntryDefinition & {
  username?: string;
  password?: string;
  workspaceName?: string;
};

export const createEntry: Journey<CreateEntryInput> = async (context, input) => {
  validate(input);
  await prepareConsoleJourney(context, input);

  const editor = context.pages.entryEditor;
  await editor.openNew(context.consoleUrl);
  await context.checkpoint({ name: Checkpoints.createEntry.formInitial, page: context.pages.page, target: editor.definitionFields });
  await editor.fillIdentity(input);
  await context.checkpoint({ name: Checkpoints.createEntry.identityComplete, page: context.pages.page, target: editor.identitySection });
  await editor.configureAppearance(input);
  await context.checkpoint({ name: Checkpoints.createEntry.appearanceComplete, page: context.pages.page, target: editor.appearanceSection });
  await editor.configureInteraction(input);
  await context.checkpoint({ name: Checkpoints.createEntry.interactionComplete, page: context.pages.page, target: editor.interactionSection });
  await editor.configurePrimaryField(input.field);
  await context.checkpoint({ name: Checkpoints.createEntry.fieldComplete, page: context.pages.page, target: editor.fieldsSection });
  await editor.bindToFlow(input.targetFlow, input.targetNamespace);
  await context.checkpoint({ name: Checkpoints.createEntry.bindingComplete, page: context.pages.page, target: editor.bindingSection });
  await editor.configureBehavior(input);
  await context.checkpoint({ name: Checkpoints.createEntry.behaviorComplete, page: context.pages.page, target: editor.behaviorSection });
  await editor.configureSuggestions(input.suggestions);
  await context.checkpoint({ name: Checkpoints.createEntry.suggestionsComplete, page: context.pages.page, target: editor.suggestionsSection });
  await context.checkpoint({ name: Checkpoints.createEntry.readyToPublish, page: context.pages.page, target: editor.definitionFields });
  await editor.publish();
  await context.checkpoint({ name: Checkpoints.createEntry.published, page: context.pages.page, target: editor.status });
};

function validate(input: CreateEntryInput): void {
  for (const property of ['name', 'displayName', 'description', 'placeholder', 'targetFlow'] as const) {
    if (!input[property]?.trim()) throw new Error(`Create Entry input '${property}' is required.`);
  }
  for (const property of ['name', 'label', 'description', 'placeholder'] as const) {
    if (!input.field[property]?.trim()) throw new Error(`Create Entry primary field '${property}' is required.`);
  }
  for (const suggestion of input.suggestions) {
    if (!suggestion.label.trim() || !suggestion.value.trim()) throw new Error('Every Entry suggestion requires a label and value.');
  }
}
