import type { Locator, Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit, selectFirstAvailable } from './controls.js';

export interface EntryFieldDefinition {
  name: string;
  label: string;
  description: string;
  placeholder: string;
  type: 'Prompt' | 'Text' | 'Textarea' | 'Conversation';
  required: boolean;
  primary: boolean;
}

export interface EntrySuggestionDefinition {
  label: string;
  value: string;
}

export interface EntryDefinition {
  name: string;
  displayName: string;
  description: string;
  presentationKind: 'Prompt' | 'Form';
  placeholder: string;
  icon?: string;
  participantsVisibility: 'Hidden' | 'Visible';
  progressVisibility: 'Hidden' | 'Compact' | 'Detailed';
  taskDisplay: 'Auto' | 'Hidden' | 'Visible';
  resultsDisplay: 'Auto' | 'Hidden' | 'Visible';
  field: EntryFieldDefinition;
  targetFlow: string;
  targetNamespace?: string;
  taskCreationMode: 'Automatic' | 'OnDemand' | 'Never';
  allowConversation: boolean;
  streamResponse: boolean;
  suggestions: readonly EntrySuggestionDefinition[];
}

export class EntryEditorPage {
  public constructor(private readonly page: Page) {}

  public async openNew(consoleUrl: string): Promise<void> {
    const response = await this.page.goto(`${consoleUrl}/entries/new?view=definition`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`New Entry page returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.locator(
      `[data-testid="${TestIds.entryEditor.definitionFields}"][data-interactive="true"]`,
    ).waitFor({ state: 'visible' });
  }

  public async fillIdentity(entry: Pick<EntryDefinition, 'name' | 'displayName' | 'description'>): Promise<void> {
    await fillAndCommit(this.page.getByTestId(TestIds.entryEditor.name), entry.name);
    await fillAndCommit(this.page.getByTestId(TestIds.entryEditor.displayName), entry.displayName);
    await fillAndCommit(this.page.getByTestId(TestIds.entryEditor.description), entry.description);
  }

  public async configureAppearance(entry: Pick<EntryDefinition, 'presentationKind' | 'placeholder' | 'icon'>): Promise<void> {
    await this.page.getByTestId(TestIds.entryEditor.presentationKind).selectOption(entry.presentationKind);
    await fillAndCommit(this.page.getByTestId(TestIds.entryEditor.placeholder), entry.placeholder);
    if (entry.icon) {
      const picker = this.appearanceSection.getByTestId(TestIds.common.iconPicker);
      await picker.locator('input[type="search"]').fill(entry.icon);
      await picker.getByRole('option', { name: entry.icon, exact: true }).click();
    }
  }

  public async configureInteraction(entry: Pick<EntryDefinition,
    'participantsVisibility' | 'progressVisibility' | 'taskDisplay' | 'resultsDisplay'>): Promise<void> {
    await this.page.getByTestId(TestIds.entryEditor.participantsVisibility).selectOption(entry.participantsVisibility);
    await this.page.getByTestId(TestIds.entryEditor.progressVisibility).selectOption(entry.progressVisibility);
    await this.page.getByTestId(TestIds.entryEditor.taskDisplay).selectOption(entry.taskDisplay);
    await this.page.getByTestId(TestIds.entryEditor.resultsDisplay).selectOption(entry.resultsDisplay);
  }

  public async configurePrimaryField(field: EntryFieldDefinition): Promise<void> {
    const card = this.page.getByTestId(TestIds.entryEditor.fieldCard).first();
    await fillAndCommit(card.getByTestId(TestIds.entryEditor.fieldName), field.name);
    await fillAndCommit(card.getByTestId(TestIds.entryEditor.fieldLabel), field.label);
    await fillAndCommit(card.getByTestId(TestIds.entryEditor.fieldDescription), field.description);
    await fillAndCommit(card.getByTestId(TestIds.entryEditor.fieldPlaceholder), field.placeholder);
    await card.getByTestId(TestIds.entryEditor.fieldType).selectOption(field.type);
    await card.getByTestId(TestIds.entryEditor.fieldRequired).setChecked(field.required);
    if (field.primary) await card.getByTestId(TestIds.entryEditor.fieldPrimary).check();
  }

  public async bindToFlow(flowName: string, namespace = 'default'): Promise<void> {
    await this.page.getByTestId(TestIds.entryEditor.targetKind).selectOption('Flow');
    const target = this.page.getByTestId(TestIds.entryEditor.targetResource);
    await target.locator('option').nth(1).waitFor({ state: 'attached' });
    await selectFirstAvailable(target, [`${namespace}:${flowName}`, flowName], 'Entry target Flow');
  }

  public async configureBehavior(entry: Pick<EntryDefinition, 'taskCreationMode' | 'allowConversation' | 'streamResponse'>): Promise<void> {
    await this.page.getByTestId(TestIds.entryEditor.taskMode).selectOption(entry.taskCreationMode);
    await this.page.getByTestId(TestIds.entryEditor.allowConversation).setChecked(entry.allowConversation);
    await this.page.getByTestId(TestIds.entryEditor.streamResponse).setChecked(entry.streamResponse);
  }

  public async configureSuggestions(suggestions: readonly EntrySuggestionDefinition[]): Promise<void> {
    for (const suggestion of suggestions) {
      await this.page.getByTestId(TestIds.entryEditor.addSuggestion).click();
      const card = this.page.getByTestId(TestIds.entryEditor.suggestionCard).last();
      await fillAndCommit(card.getByTestId(TestIds.entryEditor.suggestionLabel), suggestion.label);
      await fillAndCommit(card.getByTestId(TestIds.entryEditor.suggestionValue), suggestion.value);
    }
  }

  public async publish(): Promise<void> {
    await this.page.getByTestId(TestIds.entryEditor.publish).click();
    await this.page.locator(`[data-testid="${TestIds.entryEditor.status}"][data-state="published"]`).waitFor({ state: 'visible' });
  }

  public get definitionFields(): Locator { return this.page.getByTestId(TestIds.entryEditor.definitionFields); }
  public get identitySection(): Locator { return this.page.getByTestId(TestIds.entryEditor.identitySection); }
  public get appearanceSection(): Locator { return this.page.getByTestId(TestIds.entryEditor.appearanceSection); }
  public get interactionSection(): Locator { return this.page.getByTestId(TestIds.entryEditor.interactionSection); }
  public get fieldsSection(): Locator { return this.page.getByTestId(TestIds.entryEditor.fieldsSection); }
  public get bindingSection(): Locator { return this.page.getByTestId(TestIds.entryEditor.bindingSection); }
  public get behaviorSection(): Locator { return this.page.getByTestId(TestIds.entryEditor.behaviorSection); }
  public get suggestionsSection(): Locator { return this.page.getByTestId(TestIds.entryEditor.suggestionsSection); }
  public get status(): Locator { return this.page.getByTestId(TestIds.entryEditor.status); }
}
