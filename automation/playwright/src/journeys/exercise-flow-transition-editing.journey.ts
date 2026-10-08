import { expect } from '@playwright/test';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseFlowTransitionEditingInput {
  username?: string;
  password?: string;
}

export const exerciseFlowTransitionEditing: Journey<ExerciseFlowTransitionEditingInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const designer = context.pages.flowDesigner;
  const name = await designer.createDraftAndOpen(context.consoleUrl);
  await designer.addStep('Transform');

  await designer.dragOutputToInput('transform', 'completed');
  await designer.expectTransition('transform —completed→ completed');
  await designer.selectTransition('transform —completed→ completed');
  await designer.expectSelectedTransition('transform', 'completed', 'completed', 'transform-completed-completed');

  await designer.reconnectSelectedTransitionTarget('Error');
  await designer.expectSelectedTransition('transform', 'completed', 'error', 'transform-completed-completed');

  await designer.undo();
  await designer.expectSelectedTransition('transform', 'completed', 'completed', 'transform-completed-completed');
  await designer.redo();
  await designer.expectSelectedTransition('input', 'completed', 'completed', 'transform-completed-completed');

  await designer.expectInvalidDropDoesNotChangeTransitionCount('transform');
  await designer.save();
  await designer.open(context.consoleUrl, 'default', name);
  await designer.selectTransition('transform —completed→ error');
  await designer.expectSelectedTransition('transform', 'completed', 'error', 'transform-completed-completed');

  await designer.expectReadOnlyPublished('agentstration.assistant', 'assistant-diagnostics');
  await expect(context.pages.flowDesigner.designer).toBeVisible();
};

