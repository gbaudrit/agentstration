import { expect } from '@playwright/test';
import { Checkpoints } from '../contracts/checkpoints.js';
import { TestIds } from '../contracts/test-ids.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseBootstrapProvenanceInput {
  username?: string;
  password?: string;
  packPublisher: string;
  packName: string;
  packVersion: string;
}

export const exerciseBootstrapProvenance: Journey<ExerciseBootstrapProvenanceInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const namespace = 'agentstration.assistant';

  await context.pages.entryEditor.open(context.consoleUrl, namespace, 'ask-agentstration');
  await context.pages.entryEditor.fillIdentity({ name: 'ask-agentstration', displayName: 'Ask Agentstration (edited)', description: 'Deterministic bootstrap entry' });
  await context.pages.entryEditor.saveDraft();
  await context.checkpoint({ name: Checkpoints.bootstrapProvenance.entryEditable, page: context.pages.page, target: context.pages.entryEditor.status });

  await context.pages.flowDesigner.open(context.consoleUrl, namespace, 'assistant-diagnostics');
  await context.pages.flowDesigner.validate();
  await context.pages.flowDesigner.save();
  await context.checkpoint({ name: Checkpoints.bootstrapProvenance.flowEditable, page: context.pages.page, target: context.pages.flowDesigner.designer });

  await context.pages.agentEditor.open(context.consoleUrl, namespace, 'assistant-diagnostics');
  await context.pages.agentEditor.fillIdentity({ name: 'assistant-diagnostics', displayName: 'Assistant diagnostics (edited)', description: 'Deterministic bootstrap agent' });
  await context.pages.agentEditor.saveDraft();
  await context.checkpoint({ name: Checkpoints.bootstrapProvenance.agentEditable, page: context.pages.page, target: context.pages.agentEditor.form });

  await context.pages.distribution.createPackProject(context.consoleUrl, {
    publisher: input.packPublisher,
    name: input.packName,
    version: input.packVersion,
    displayName: 'Bootstrap provenance fixture',
    resourceKind: 'Entry',
    resourceName: 'ask-agentstration',
  });
  await context.pages.distribution.buildPackProject();
  const packNamespace = await context.pages.distribution.installPackProject();
  await context.pages.entryEditor.open(context.consoleUrl, packNamespace, 'ask-agentstration');
  await expect(context.pages.entryEditor.definitionFields).toHaveAttribute('disabled', '');
  await context.checkpoint({ name: Checkpoints.bootstrapProvenance.packReadOnly, page: context.pages.page, target: context.pages.entryEditor.definitionFields });
};
