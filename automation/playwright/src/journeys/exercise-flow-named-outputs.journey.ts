import { expect } from '@playwright/test';
import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseFlowNamedOutputsInput {
  username?: string;
  password?: string;
}

export const exerciseFlowNamedOutputs: Journey<ExerciseFlowNamedOutputsInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const designer = context.pages.flowDesigner;
  const fixture = await designer.createNamedOutputCompositionAndOpen(context.consoleUrl);

  await designer.expectPublishedOutputContracts(context.consoleUrl, fixture.childName);
  await designer.expectNamedPorts('Child review', [
    { event: 'approved', outcome: 'success' },
    { event: 'rejected', outcome: 'success' },
    { event: 'provider-error', outcome: 'error' },
  ]);
  await context.checkpoint({ name: Checkpoints.flowNamedOutputs.activePorts, page: context.pages.page, target: designer.designer });

  await designer.selectStep('Child review');
  await designer.useFlowCallVersion('Exact', '1.0.0');
  await designer.expectNamedPorts('Child review', [
    { event: 'historical-result', outcome: 'success' },
    { event: 'legacy-failure', outcome: 'error' },
  ]);
  await context.checkpoint({ name: Checkpoints.flowNamedOutputs.historicalPorts, page: context.pages.page, target: designer.designer });

  await designer.useFlowCallVersion('Exact', '2.0.0');
  await designer.expectNamedPorts('Child review', [
    { event: 'approved', outcome: 'success' },
    { event: 'rejected', outcome: 'success' },
    { event: 'provider-error', outcome: 'error' },
  ]);
  await designer.useFlowCallVersion('Active');

  await designer.addNamedOutput({
    name: 'approved-parent', displayName: 'Approved parent', outcome: 'Success',
    mapping: '"${transition.output}"',
    schema: '{"type":"object","properties":{"decision":{"type":"string"}}}',
  });
  await designer.addNamedOutput({
    name: 'error-parent', displayName: 'Error parent', outcome: 'Error',
    mapping: '"${transition.output}"',
    schema: '{"type":"object","properties":{"retryable":{"type":"boolean"}}}',
    code: 'PARENT_PROVIDER_ERROR', message: 'The child provider failed.', details: '${transition.output.details}',
  });
  await context.checkpoint({ name: Checkpoints.flowNamedOutputs.outputsAuthored, page: context.pages.page, target: designer.designer });

  await designer.dragNamedOutputToInput('Child review', 'approved', 'Approved parent');
  await designer.expectTransition('review —approved→ approved-parent');
  await designer.selectTransition('review —approved→ approved-parent');
  await designer.setSelectedTransitionPriority(7);
  await designer.changeSelectedTransitionEvent('rejected');
  await designer.expectSelectedTransition('review', 'rejected', 'approved-parent', 'review-approved-approved-parent');
  await designer.expectSelectedTransitionPriority(7);

  await designer.selectStep('Approved parent');
  await designer.renameSelectedOutput('final-approved');
  await designer.expectTransition('review —rejected→ final-approved');
  await designer.undo();
  await designer.expectTransition('review —rejected→ approved-parent');
  await designer.redo();
  await designer.expectTransition('review —rejected→ final-approved');

  await designer.dragNamedOutputToInput('Child review', 'provider-error', 'Error parent');
  await designer.expectTransition('review —provider-error→ error-parent');
  await designer.expectInvalidDropDoesNotChangeTransitionCount('Child review');
  await designer.validate();
  await expect(designer.designer.locator('.validation-dock li.error')).toHaveCount(0);
  await designer.save();
  await designer.expectAutosaved();
  await context.checkpoint({ name: Checkpoints.flowNamedOutputs.transitionsConnected, page: context.pages.page, target: designer.designer });

  await designer.open(context.consoleUrl, 'default', fixture.parentName);
  await designer.expectTransition('review —rejected→ final-approved');
  await designer.expectTransition('review —provider-error→ error-parent');
  await designer.publishAsReadOnlyFixture(context.consoleUrl, fixture.parentName);
  await designer.expectReadOnlyPublished(context.consoleUrl, 'default', fixture.parentName);
  await context.checkpoint({ name: Checkpoints.flowNamedOutputs.publishedReadOnly, page: context.pages.page, target: designer.designer });
};
